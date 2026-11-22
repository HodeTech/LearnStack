using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Application.DataProtection;

/// <summary>
/// Builds one export bundle as a single JSON document streamed into chunk-sized plaintext segments
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 3</see>).
/// </summary>
/// <remarks>
/// <para>The document is, in this order:</para>
/// <code>
/// { "schema_version": 1,
///   "request": { "id", "kind", "origin", "scope" },
///   "generated_at": "…Z",
///   "sections": { "&lt;participant&gt;": { "&lt;collection&gt;": [ record, … ], … }, … },
///   "truncated": { "&lt;participant&gt;.&lt;collection&gt;": { "included": n, "total": m } } }
/// </code>
/// <para>
/// Every registered participant has a section, an empty one included, in registry order. A section writes its
/// collections one after another: returning to a collection it has left is a programming error, because the
/// document is streamed and never reordered.
/// </para>
/// <para>
/// <b>Bounds.</b> A record is admitted only if it fits both its section's cap and the bundle ceiling less a
/// fixed reserve for the closing structure, so the finished document never passes <c>DataExport:MaxBytes</c>.
/// The section cap and the record's collection cap are checked first: a record that would pass one of them and the
/// ceiling is a truncation of its collection, which the participant declares, not a failure of the request. A record
/// is validated as exactly one JSON value before any byte of it is written, so the bundle never holds a partial
/// record.
/// </para>
/// <para>
/// <b>A cap truncates, whatever the ceiling.</b> Every declared section and collection cap is clamped to half the
/// configured ceiling (<see cref="EffectiveCap"/>). At the default 16 MiB ceiling the audit section's 8 MiB is
/// unchanged; at the smallest admitted ceiling (1 MiB) an unclamped 8 MiB cap would never apply and a long audit
/// history would fail the request permanently with <c>export_too_large</c>. The clamp is a bound, not a budget: the
/// uncapped sections and two capped ones together can still reach the ceiling, and that genuine case stays
/// <c>export_too_large</c>.
/// </para>
/// </remarks>
public sealed class DataExportBundleBuilder(IUserDataParticipantRegistry registry, DataExportOptions options) : IDataExportBundleBuilder
{
    /// <summary>The bundle schema version; a change to the document's shape is a new version.</summary>
    public const int SchemaVersion = 1;

    private const string InstantFormat = "yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fffffff'Z'";

    public async Task<DataExportBundleResult> BuildAsync(UserDataSubject subject, DateTimeOffset generatedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (generatedAt == default || generatedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("A bundle's generation time is a UTC instant.", nameof(generatedAt));
        }

        if (subject.Kind != DataRequestKind.Export)
        {
            throw new ArgumentException("Only an export request builds a bundle.", nameof(subject));
        }

        var buffer = new PlaintextSegmentBuffer(options.MaxBytes);
        var built = false;
        try
        {
            var state = new BundleState(buffer, options.MaxBytes - DataRequestLimits.BundleTailReserveBytes);
            using (state.Json)
            {
                WriteHeader(state.Json, subject, generatedAt);
                state.Json.WriteStartObject("sections");
                foreach (var participant in registry.Participants)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var section = new SectionWriter(state, participant.Key, SectionCap(participant, options.MaxBytes),
                        CollectionCaps(participant, options.MaxBytes));
                    state.Json.WritePropertyName(participant.Key);
                    state.Json.WriteStartObject();
                    try
                    {
                        await participant.ExportAsync(subject, section, cancellationToken).ConfigureAwait(false);
                    }
                    finally
                    {
                        section.Close();
                    }

                    if (state.TooLarge)
                    {
                        return DataExportBundleResult.TooLarge;
                    }

                    section.Finish();
                    state.Json.WriteEndObject();
                }

                state.Json.WriteEndObject();
                var truncations = DataExportTruncation.Normalize(state.Truncations);
                state.Json.WriteStartObject("truncated");
                foreach (var truncation in truncations)
                {
                    state.Json.WriteStartObject(truncation.Collection);
                    state.Json.WriteNumber("included", truncation.Included);
                    state.Json.WriteNumber("total", truncation.Total);
                    state.Json.WriteEndObject();
                }

                state.Json.WriteEndObject();
                state.Json.WriteEndObject();
                state.Json.Flush();
                if (buffer.Overflowed || buffer.Length is < 1 || buffer.Length > options.MaxBytes)
                {
                    return DataExportBundleResult.TooLarge;
                }

                built = true;
                return DataExportBundleResult.Built(new Bundle(buffer, truncations));
            }
        }
        finally
        {
            if (!built)
            {
                buffer.Dispose();
            }
        }
    }

    private static void WriteHeader(Utf8JsonWriter json, UserDataSubject subject, DateTimeOffset generatedAt)
    {
        json.WriteStartObject();
        json.WriteNumber("schema_version", SchemaVersion);
        json.WriteStartObject("request");
        json.WriteString("id", subject.RequestId.ToString("D"));
        json.WriteString("kind", DataRequestCodes.Kinds.ToCode(subject.Kind));
        json.WriteString("origin", DataRequestCodes.Origins.ToCode(subject.Origin));
        json.WriteString("scope", subject.Group.IsTenantWide ? "tenant" : "organization");
        json.WriteEndObject();
        json.WriteString("generated_at", generatedAt.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture));
    }

    /// <summary>A declared cap, clamped to half the ceiling so that it truncates before the ceiling refuses the bundle.</summary>
    public static long EffectiveCap(long declared, long ceiling) =>
        declared is >= 1 and <= DataRequestLimits.MaximumBundleBytes
            ? Math.Min(declared, ceiling / 2)
            : throw new InvalidOperationException("A participant's export cap is outside the bundle's range.");

    private static long SectionCap(IUserDataParticipant participant, long ceiling) =>
        participant.ExportSectionCapBytes is { } cap ? EffectiveCap(cap, ceiling) : long.MaxValue;

    private static Dictionary<string, long> CollectionCaps(IUserDataParticipant participant, long ceiling)
    {
        var caps = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (collection, cap) in participant.ExportCollectionCapBytes
                     ?? throw new InvalidOperationException("A participant's collection caps are a collection."))
        {
            if (!DataRequestLimits.IsCollectionName(collection))
            {
                throw new InvalidOperationException("A collection cap names a collection of its section.");
            }

            caps.Add(collection, EffectiveCap(cap, ceiling));
        }

        return caps;
    }

    private sealed class BundleState
    {
        public BundleState(PlaintextSegmentBuffer buffer, long payloadLimit)
        {
            Json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false, SkipValidation = false });
            PayloadLimit = payloadLimit;
        }

        public Utf8JsonWriter Json { get; }

        /// <summary>The ceiling less the closing reserve: what records and their framing may use.</summary>
        public long PayloadLimit { get; }

        public long Written => Json.BytesCommitted + Json.BytesPending;

        /// <summary>Sticky: once a record was refused for the ceiling, the bundle is refused.</summary>
        public bool TooLarge { get; set; }

        public List<DataExportTruncation> Truncations { get; } = [];
    }

    /// <summary>One participant's bounded, append-only section; unusable once its participant returned.</summary>
    private sealed class SectionWriter(BundleState state, string section, long cap, IReadOnlyDictionary<string, long> collectionCaps)
        : IUserDataExportWriter
    {
        private readonly HashSet<string> _closedCollections = new(StringComparer.Ordinal);
        private readonly HashSet<string> _declared = new(StringComparer.Ordinal);
        private string? _current;
        private long _sectionBytes;
        private long _collectionBytes;
        private bool _closed;

        /// <summary>What the section may still append, before the open collection's own cap.</summary>
        public long RemainingBytes => _closed || state.TooLarge
            ? 0
            : Math.Max(0, Math.Min(cap - _sectionBytes, state.PayloadLimit - state.Written));

        public ValueTask<UserDataExportAppend> AppendAsync(string collection, ReadOnlyMemory<byte> utf8JsonRecord,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Append(collection, utf8JsonRecord.Span));
        }

        public void DeclareTruncated(string collection, long includedRecords, long totalRecords)
        {
            RequireOpen();
            if (!DataRequestLimits.IsCollectionName(collection))
            {
                throw new ArgumentException("A truncation names a collection of its section.", nameof(collection));
            }

            if (!_declared.Add(collection))
            {
                throw new InvalidOperationException("A collection's truncation is declared once.");
            }

            if (state.Truncations.Count >= DataRequestLimits.MaximumTruncatedCollections)
            {
                throw new InvalidOperationException("A bundle states at most the bounded number of truncated collections.");
            }

            state.Truncations.Add(new DataExportTruncation(section + "." + collection, includedRecords, totalRecords));
        }

        /// <summary>Refuses every later call, whether the participant returned or threw.</summary>
        public void Close() => _closed = true;

        /// <summary>Closes the open collection once the participant returned normally.</summary>
        public void Finish()
        {
            if (_current is not null)
            {
                state.Json.WriteEndArray();
                _current = null;
            }
        }

        private UserDataExportAppend Append(string collection, ReadOnlySpan<byte> record)
        {
            RequireOpen();
            if (!DataRequestLimits.IsCollectionName(collection))
            {
                throw new ArgumentException("A record names a collection of its section.", nameof(collection));
            }

            if (state.TooLarge)
            {
                return UserDataExportAppend.BundleCeilingReached;
            }

            var opening = !string.Equals(_current, collection, StringComparison.Ordinal);
            if (opening && _closedCollections.Contains(collection))
            {
                throw new InvalidOperationException("A section writes its collections one after another and never returns to one.");
            }

            RequireSingleValue(record);

            // An upper bound of what the record adds: itself and its separating comma, and for a new collection
            // the previous collection's closing bracket, a comma, the quoted name, the colon and the bracket.
            var cost = record.Length + 1L + (opening ? collection.Length + 6L : 0L);
            var collectionBytes = opening ? 0L : _collectionBytes;
            if (_sectionBytes + cost > cap
                || collectionCaps.TryGetValue(collection, out var collectionCap) && collectionBytes + cost > collectionCap)
            {
                return UserDataExportAppend.SectionCapReached;
            }

            if (state.Written + cost > state.PayloadLimit)
            {
                state.TooLarge = true;
                return UserDataExportAppend.BundleCeilingReached;
            }

            if (opening)
            {
                if (_current is not null)
                {
                    state.Json.WriteEndArray();
                    _closedCollections.Add(_current);
                }

                state.Json.WritePropertyName(collection);
                state.Json.WriteStartArray();
                _current = collection;
            }

            state.Json.WriteRawValue(record, skipInputValidation: true);
            _sectionBytes += cost;
            _collectionBytes = collectionBytes + cost;
            return UserDataExportAppend.Appended;
        }

        private void RequireOpen()
        {
            if (_closed)
            {
                throw new InvalidOperationException("A section writer is used only while its participant exports.");
            }
        }

        private static void RequireSingleValue(ReadOnlySpan<byte> record)
        {
            if (record.IsEmpty)
            {
                throw new ArgumentException("An export record is one complete JSON value.", nameof(record));
            }

            var reader = new Utf8JsonReader(record, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow });
            try
            {
                if (!reader.Read())
                {
                    throw new ArgumentException("An export record is one complete JSON value.", nameof(record));
                }

                reader.Skip();
                if (reader.Read())
                {
                    throw new ArgumentException("An export record is one complete JSON value.", nameof(record));
                }
            }
            catch (JsonException)
            {
                throw new ArgumentException("An export record is one complete JSON value.", nameof(record));
            }
        }
    }

    private sealed class Bundle(PlaintextSegmentBuffer buffer, ImmutableArray<DataExportTruncation> truncations) : IDataExportBundle
    {
        public long ByteCount { get; } = buffer.Length;

        public int ChunkCount { get; } = DataRequestLimits.ChunkCountFor(buffer.Length);

        public ImmutableArray<DataExportTruncation> Truncations { get; } = truncations;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadChunksAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var chunk in buffer.Chunks())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return chunk;
                await Task.CompletedTask.ConfigureAwait(false);
            }
        }

        public void Dispose() => buffer.Dispose();

        public override string ToString() => nameof(IDataExportBundle);
    }
}
