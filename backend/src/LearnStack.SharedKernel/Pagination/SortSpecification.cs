using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;

namespace LearnStack.SharedKernel.Pagination;

/// <summary>Which way one sort key orders.</summary>
public enum SortDirection
{
    Ascending,
    Descending,
}

/// <summary>One key of an ordered sort, already parsed.</summary>
public sealed record SortKey(string Field, SortDirection Direction);

/// <summary>
/// The parsed form of the <c>sort</c> query parameter that
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Filtering and Sorting</see> specifies: <c>sort=field</c>,
/// <c>sort=-field</c> for descending, and <c>sort=-publishedAt,title</c> for
/// several keys in priority order.
/// </summary>
/// <remarks>
/// <para>
/// It lives in the kernel rather than the API layer because a sort order is
/// consumed by whoever builds the query, and — like
/// <see cref="CursorPagination"/> — that is a handler, not a controller. The
/// wire layer parses; the handler applies.
/// </para>
/// <para>
/// Parsing and <b>authorising</b> are separate steps on purpose.
/// <see cref="TryParse"/> answers "is this well formed"; <see cref="Restrict"/>
/// answers "may this endpoint sort by that", which only the endpoint knows.
/// Collapsing them would mean either a kernel that knows every resource's
/// fields, or an endpoint that accepts any field a client names.
/// </para>
/// </remarks>
public sealed record SortSpecification
{
    /// <summary>
    /// The most keys one request may name. A sort is a query plan, and each
    /// key is an index decision; an unbounded list lets a client compose an
    /// arbitrarily expensive ordering. Four covers every ordering the corpus
    /// describes with room to spare.
    /// </summary>
    public const int MaxKeys = 4;

    private SortSpecification(IReadOnlyList<SortKey> keys) => Keys = keys;

    /// <summary>No sort requested; the endpoint's default ordering applies.</summary>
    public static SortSpecification Empty { get; } = new([]);

    /// <summary>Keys in the order the client gave them, which is priority order.</summary>
    public IReadOnlyList<SortKey> Keys { get; }

    public bool IsEmpty => Keys.Count == 0;

    /// <summary>
    /// Parses the raw <c>sort</c> value. Returns <c>false</c> and names the
    /// offending segment rather than throwing — a malformed sort is a client
    /// error, and the caller needs the segment to say which part was wrong.
    /// </summary>
    public static bool TryParse(
        string? raw,
        out SortSpecification specification,
        [NotNullWhen(false)] out string? offendingSegment)
    {
        specification = Empty;
        offendingSegment = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var segments = raw.Split(',');
        if (segments.Length > MaxKeys)
        {
            offendingSegment = raw;
            return false;
        }

        var keys = new List<SortKey>(segments.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            var direction = trimmed.StartsWith('-') ? SortDirection.Descending : SortDirection.Ascending;
            var field = direction == SortDirection.Descending ? trimmed[1..] : trimmed;

            // An empty segment is what a trailing comma or `a,,b` produces. It
            // is a typo, not an empty sort — accepting it would silently drop
            // a key the client believes it asked for.
            if (!IsWellFormedField(field) || !seen.Add(field))
            {
                offendingSegment = segment;
                return false;
            }

            keys.Add(new SortKey(field, direction));
        }

        specification = new SortSpecification(keys);
        return true;
    }

    /// <summary>
    /// Narrows the specification to the fields an endpoint permits. An
    /// unpermitted field is a <c>validation_failed</c> result rather than a
    /// silently ignored key: a client that believes it sorted and did not gets
    /// a page in an order it did not ask for, and no way to notice.
    /// </summary>
    public Result<SortSpecification> Restrict(IReadOnlyCollection<string> allowedFields)
    {
        ArgumentNullException.ThrowIfNull(allowedFields);

        var rejected = Keys
            .Where(key => !allowedFields.Contains(key.Field, StringComparer.OrdinalIgnoreCase))
            .Select(key => key.Field)
            .ToList();

        if (rejected.Count == 0)
        {
            return Result<SortSpecification>.Ok(this);
        }

        return Result<SortSpecification>.Fail(new Error(
            new LocalizedMessage("lockey_validation_failed"),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
            {
                ["sort"] = [.. rejected.Select(field => new LocalizedMessage(
                    "lockey_sort_field_not_allowed",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["field"] = field,
                    }))],
            }));
    }

    /// <summary>
    /// A field is one or more segments of letters and digits joined by dots,
    /// starting with a letter — the camelCase shape Standards 04 § Style fixes
    /// for bodies, with a dot for a nested path. Anything else is refused here
    /// rather than left for the endpoint's allow-list, so a value that could
    /// only have come from a malformed client never travels further in.
    /// </summary>
    private static bool IsWellFormedField(string field)
    {
        if (field.Length == 0 || field.Length > 64)
        {
            return false;
        }

        var expectingFirstOfSegment = true;
        foreach (var character in field)
        {
            if (character == '.')
            {
                if (expectingFirstOfSegment)
                {
                    return false;
                }

                expectingFirstOfSegment = true;
                continue;
            }

            if (expectingFirstOfSegment && !char.IsAsciiLetter(character))
            {
                return false;
            }

            if (!char.IsAsciiLetterOrDigit(character))
            {
                return false;
            }

            expectingFirstOfSegment = false;
        }

        return !expectingFirstOfSegment;
    }
}
