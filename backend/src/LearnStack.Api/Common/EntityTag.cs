using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace LearnStack.Api.Common;

/// <summary>
/// Optimistic concurrency over <c>ETag</c> / <c>If-Match</c>, per
/// <see href="../../../../docs/standards/04-api-design.md">Standards 04
/// § Optimistic Concurrency</see>: a mutable resource exposes an
/// <c>ETag</c>, a conditional write sends it back in <c>If-Match</c>, and a
/// version mismatch is <b>409</b> with <c>concurrency_conflict</c>.
/// </summary>
/// <remarks>
/// <para>
/// No resource has a version yet — the first ones arrive with the tenancy
/// schema in Packet 6 — so this is the mechanism and its rules, not a wiring
/// into any endpoint. It ships now because the rules are the part worth
/// deciding once: which comparison is used, what a missing header means, and
/// what a client that sends <c>*</c> is asking for.
/// </para>
/// <para>
/// <b>Strong comparison, always.</b> RFC 9110 § 13.1.1 requires it for
/// <c>If-Match</c>, and it is the only comparison that means anything here: a
/// weak tag says "semantically equivalent", and two versions of a row that are
/// semantically equivalent are still two versions, one of which the client did
/// not see.
/// </para>
/// </remarks>
public static class EntityTag
{
    /// <summary>
    /// Formats a version as a strong entity tag. The quoting is not
    /// decoration — an unquoted value is not a valid entity tag and a client
    /// echoing it back produces a header the server cannot parse.
    /// </summary>
    public static string For(long version) => $"\"{version}\"";

    /// <summary>
    /// What a conditional request asked for.
    /// </summary>
    public enum Precondition
    {
        /// <summary>No <c>If-Match</c>; the caller made no claim about the version.</summary>
        Absent,

        /// <summary>The caller matched the current version.</summary>
        Matched,

        /// <summary>The caller matched a different version, or sent a malformed header.</summary>
        Failed,

        /// <summary><c>If-Match: *</c> — "whatever version, as long as it exists".</summary>
        AnyExisting,
    }

    /// <summary>
    /// Evaluates <c>If-Match</c> against the resource's current version.
    /// </summary>
    /// <remarks>
    /// A malformed header is <see cref="Precondition.Failed"/>, not
    /// <see cref="Precondition.Absent"/>. Treating "I could not parse your
    /// precondition" as "you did not send one" turns a conditional write into
    /// an unconditional one — the exact overwrite the client was trying to
    /// prevent.
    /// </remarks>
    public static Precondition Evaluate(HttpRequest request, long currentVersion)
    {
        ArgumentNullException.ThrowIfNull(request);

        var raw = request.Headers.IfMatch;
        if (raw.Count == 0)
        {
            return Precondition.Absent;
        }

        if (!EntityTagHeaderValue.TryParseStrictList(raw, out var tags) || tags.Count == 0)
        {
            return Precondition.Failed;
        }

        var current = new EntityTagHeaderValue(For(currentVersion));

        foreach (var tag in tags)
        {
            if (tag.Equals(EntityTagHeaderValue.Any))
            {
                return Precondition.AnyExisting;
            }

            // Strong comparison: a weak tag never matches, even against itself.
            if (!tag.IsWeak && tag.Compare(current, useStrongComparison: true))
            {
                return Precondition.Matched;
            }
        }

        return Precondition.Failed;
    }

    /// <summary>
    /// The failure a mismatched precondition produces: <b>409</b> with
    /// <c>concurrency_conflict</c>, per Standards 04.
    /// </summary>
    /// <remarks>
    /// 409 rather than the 412 Precondition Failed that RFC 9110 describes for
    /// <c>If-Match</c>. That is a deliberate departure and it is Standards 04's
    /// call, not this file's: the corpus already maps
    /// <c>concurrency_conflict</c> to 409 in <c>HttpStatusMap</c> and lists 409
    /// — not 412 — in its status table, so emitting 412 here would put a status
    /// on the wire that no error code maps to and that the generated SDK has no
    /// branch for.
    /// </remarks>
    public static Error ConflictError() =>
        new(new LocalizedMessage("lockey_concurrency_conflict"));

    /// <summary>Sets the <c>ETag</c> response header for a version.</summary>
    public static void SetEntityTag(HttpResponse response, long version)
    {
        ArgumentNullException.ThrowIfNull(response);
        response.Headers.ETag = For(version);
    }
}
