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

/// <summary>
/// One term of an ordered sort, already parsed.
/// </summary>
/// <remarks>
/// Named <c>SortTerm</c> rather than the more obvious <c>SortKey</c> because
/// <c>System.Globalization.SortKey</c> exists: any file importing both
/// namespaces — and the API layer already imports
/// <c>System.Globalization</c> — would fail to compile on an ambiguous
/// reference. Cheap to avoid now, impossible once anything consumes it.
/// </remarks>
public sealed record SortTerm(string Field, SortDirection Direction);

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
    /// The most terms one request may name. A sort is a query plan, and each
    /// term is an index decision; an unbounded list lets a client compose an
    /// arbitrarily expensive ordering. Four covers every ordering the corpus
    /// describes with room to spare.
    /// </summary>
    public const int MaxTerms = 4;

    /// <summary>
    /// The <c>errors</c> map key a sort failure is reported under — the name
    /// the client sent. Stated once here so the kernel, the wire type and the
    /// standard cannot drift to three spellings.
    /// </summary>
    public const string ErrorsKey = "sort";

    private SortSpecification(IReadOnlyList<SortTerm> terms) => Terms = terms;

    /// <summary>
    /// Structural equality by hand. A positional/record <c>Equals</c> compares
    /// <see cref="Terms"/> by reference, so two specifications parsed from the
    /// same string would compare unequal — the same trap
    /// <see cref="Results.Error"/> documents and overrides for.
    /// </summary>
    public bool Equals(SortSpecification? other) =>
        other is not null && Terms.SequenceEqual(other.Terms);

    public override int GetHashCode()
    {
        var hash = default(HashCode);
        foreach (var term in Terms)
        {
            hash.Add(term);
        }

        return hash.ToHashCode();
    }

    /// <summary>No sort requested; the endpoint's default ordering applies.</summary>
    public static SortSpecification Empty { get; } = new(Array.Empty<SortTerm>());

    /// <summary>Terms in the order the client gave them, which is priority order.</summary>
    public IReadOnlyList<SortTerm> Terms { get; }

    public bool IsEmpty => Terms.Count == 0;

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

        // Count before splitting. Split materialises every segment first, so
        // an 8 KB `sort` allocated tens of kilobytes before the guard that
        // exists to reject it — the guard paid for the attack it prevents.
        if (raw.AsSpan().Count(',') >= MaxTerms)
        {
            offendingSegment = raw;
            return false;
        }

        var segments = raw.Split(',');

        var terms = new List<SortTerm>(segments.Length);
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

            terms.Add(new SortTerm(field, direction));
        }

        specification = new SortSpecification(terms.AsReadOnly());
        return true;
    }

    /// <summary>
    /// Narrows the specification to the fields an endpoint permits. An
    /// unpermitted field is a <c>validation_failed</c> result rather than a
    /// silently ignored key: a client that believes it sorted and did not gets
    /// a page in an order it did not ask for, and no way to notice.
    /// </summary>
    // There is deliberately no MalformedSortError here. One was written, and
    // it was dead on arrival: a malformed `sort` is caught by the wire type's
    // IValidatableObject, and [ApiController]'s automatic 400 answers before
    // any action runs — so the richer body, naming the offending segment,
    // never reached a client. A grammar failure now answers exactly as every
    // other binding failure does, which is also the more consistent contract:
    // `?limit=abc` and `?sort=title,` produce the same shape under different
    // keys. The segment TryParse computes is still returned to the caller for
    // logging; it is simply not a wire field.

    public Result<SortSpecification> Restrict(IReadOnlyCollection<string> allowedFields)
    {
        ArgumentNullException.ThrowIfNull(allowedFields);

        var rejected = Terms
            .Where(term => !allowedFields.Contains(term.Field, StringComparer.OrdinalIgnoreCase))
            .Select(term => term.Field)
            .ToList();

        if (rejected.Count == 0)
        {
            // Canonicalise to the allow-list's spelling. The match is
            // case-insensitive, so `?sort=PublishedAt` is accepted — and a
            // handler that switches on the field name, or builds an EF
            // OrderBy from it, would then be handed a string the endpoint
            // never declared. Approving one spelling and returning another is
            // the sort of asymmetry that fails once, in production, on a
            // field nobody thought to test in mixed case.
            var canonical = Terms
                .Select(term => new SortTerm(
                    allowedFields.First(allowed =>
                        string.Equals(allowed, term.Field, StringComparison.OrdinalIgnoreCase)),
                    term.Direction))
                .ToList();

            return Result<SortSpecification>.Ok(new SortSpecification(canonical.AsReadOnly()));
        }

        return Result<SortSpecification>.Fail(new Error(
            new LocalizedMessage("lockey_validation_failed"),
            new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
            {
                [ErrorsKey] = [.. rejected.Select(field => new LocalizedMessage(
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
