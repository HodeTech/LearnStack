using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Modules.Identity.Domain;

/// <summary>
/// The canonical JSON of a request's three bounded documents: the frozen Membership snapshot, the
/// participant progress record and the closed retained-reference report.
/// </summary>
/// <remarks>
/// <para>
/// Written in one canonical form (sorted, lower case, fixed instant format) so that the stored value
/// is a function of its content, and read strictly: a stored document the CHECKs would not admit is
/// corrupt storage and throws, never a partially read value. The CHECKs on
/// <c>data_subject_requests</c> repeat these shapes, because <c>jsonb</c> is otherwise unbounded.
/// </para>
/// <para>
/// None holds a name, an address or any content: the snapshot holds pseudonymous Membership
/// identifiers, the progress record participant keys and instants, the report closed kinds and counts
/// (ADR-0106 § 9).
/// </para>
/// </remarks>
public static class DataRequestDocuments
{
    /// <summary>The fixed UTC instant format of a progress entry, at the precision of a .NET tick.</summary>
    public const string InstantFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

    public const string EmptyObject = "{}";

    /// <summary>Writes a non-empty, duplicate-free, bounded set of Membership identifiers, sorted.</summary>
    /// <returns><see langword="null"/> when the set is empty, duplicated, unassigned or too large.</returns>
    public static string? WriteMemberships(IReadOnlyCollection<MembershipId> memberships)
    {
        ArgumentNullException.ThrowIfNull(memberships);
        if (memberships.Count is < 1 or > DataRequestLimits.MaximumSnapshotMemberships
            || memberships.Any(member => !member.IsInitialized() || member.Value == Guid.Empty || member.Value.Version != 7)
            || memberships.Distinct().Count() != memberships.Count)
        {
            return null;
        }

        return JsonSerializer.Serialize(memberships.Select(member => member.Value.ToString("D"))
            .Order(StringComparer.Ordinal).ToArray());
    }

    /// <exception cref="InvalidOperationException">The stored snapshot is not a canonical bounded set.</exception>
    public static ImmutableArray<MembershipId> ReadMemberships(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw Corrupt();
            }

            var memberships = document.RootElement.EnumerateArray().Select(element =>
                element.ValueKind == JsonValueKind.String && Guid.TryParseExact(element.GetString(), "D", out var id)
                    && id.Version == 7 && string.Equals(element.GetString(), id.ToString("D"), StringComparison.Ordinal)
                    ? MembershipId.From(id)
                    : throw Corrupt()).ToImmutableArray();
            return WriteMemberships(memberships) is null ? throw Corrupt() : memberships;
        }
        catch (JsonException)
        {
            throw Corrupt();
        }
    }

    /// <summary>Writes a progress record: participant key to its completion instant, sorted by key.</summary>
    /// <returns><see langword="null"/> when a key is malformed, an instant is not UTC or the record is too large.</returns>
    public static string? WriteProgress(IReadOnlyDictionary<string, DateTimeOffset> progress)
    {
        ArgumentNullException.ThrowIfNull(progress);
        if (progress.Count > DataRequestLimits.MaximumParticipants
            || progress.Any(entry => !DataRequestLimits.IsParticipantKey(entry.Key)
                || entry.Value == default || entry.Value.Offset != TimeSpan.Zero))
        {
            return null;
        }

        var ordered = progress.OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Key, entry => entry.Value.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture),
                StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered);
    }

    /// <exception cref="InvalidOperationException">The stored record is malformed.</exception>
    public static FrozenDictionary<string, DateTimeOffset> ReadProgress(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Corrupt();
            }

            var progress = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.String
                    || !DateTime.TryParseExact(entry.Value.GetString(), InstantFormat, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant)
                    || !progress.TryAdd(entry.Name, new DateTimeOffset(instant, TimeSpan.Zero)))
                {
                    throw Corrupt();
                }
            }

            return WriteProgress(progress) is null ? throw Corrupt() : progress.ToFrozenDictionary(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            throw Corrupt();
        }
    }

    /// <summary>Writes a closed retained-reference report: kind code to count, sorted by code.</summary>
    /// <returns><see langword="null"/> when a kind repeats or a count is negative.</returns>
    public static string? WriteResult(IReadOnlyCollection<RetainedReferenceCount> retained)
    {
        ArgumentNullException.ThrowIfNull(retained);
        if (retained.Any(entry => entry is null) || retained.Select(entry => entry.Kind).Distinct().Count() != retained.Count)
        {
            return null;
        }

        var ordered = retained.ToDictionary(entry => DataRequestCodes.RetainedKinds.ToCode(entry.Kind), entry => entry.Count,
                StringComparer.Ordinal)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
        return JsonSerializer.Serialize(ordered);
    }

    /// <exception cref="InvalidOperationException">The stored report is malformed.</exception>
    public static FrozenDictionary<RetainedReferenceKind, long> ReadResult(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Corrupt();
            }

            var result = new Dictionary<RetainedReferenceKind, long>();
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!DataRequestCodes.RetainedKinds.TryParse(entry.Name, out var kind)
                    || entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetInt64(out var count)
                    || count < 0 || !result.TryAdd(kind, count))
                {
                    throw Corrupt();
                }
            }

            return result.ToFrozenDictionary();
        }
        catch (JsonException)
        {
            throw Corrupt();
        }
    }

    /// <summary>
    /// Writes an artifact's truncation statement: <c>participant.collection</c> to its included and total
    /// record counts, sorted by key (ADR-0107 § 3). An empty statement is <c>{}</c>.
    /// </summary>
    /// <returns><see langword="null"/> when a key repeats or the statement passes its bound.</returns>
    public static string? WriteTruncation(IReadOnlyCollection<DataExportTruncation> truncations)
    {
        ArgumentNullException.ThrowIfNull(truncations);
        ImmutableArray<DataExportTruncation> ordered;
        try
        {
            ordered = DataExportTruncation.Normalize(truncations);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return JsonSerializer.Serialize(ordered.ToDictionary(item => item.Collection,
            item => new SortedDictionary<string, long>(StringComparer.Ordinal) { ["included"] = item.Included, ["total"] = item.Total },
            StringComparer.Ordinal));
    }

    /// <exception cref="InvalidOperationException">The stored statement is malformed.</exception>
    public static ImmutableArray<DataExportTruncation> ReadTruncation(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw Corrupt();
            }

            var items = new List<DataExportTruncation>();
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object || entry.Value.EnumerateObject().Count() != 2
                    || !entry.Value.TryGetProperty("included", out var included) || !included.TryGetInt64(out var includedCount)
                    || !entry.Value.TryGetProperty("total", out var total) || !total.TryGetInt64(out var totalCount))
                {
                    throw Corrupt();
                }

                items.Add(new DataExportTruncation(entry.Name, includedCount, totalCount));
            }

            return DataExportTruncation.Normalize(items);
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException)
        {
            throw Corrupt();
        }
    }

    private static InvalidOperationException Corrupt() => new("A stored data request document is malformed.");
}
