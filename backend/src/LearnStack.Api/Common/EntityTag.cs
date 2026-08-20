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
    /// <remarks>
    /// Invariant, because <see cref="ReadAssertion"/> parses with
    /// <see cref="System.Globalization.NumberStyles.None"/> and
    /// <see cref="System.Globalization.CultureInfo.InvariantCulture"/>. A tag
    /// minted under the server's locale and parsed under the invariant one is
    /// an asymmetry waiting for the first culture whose negative sign or digits
    /// differ — and this team's default culture is already not the invariant
    /// one.
    /// </remarks>
    public static string For(long version) =>
        $"\"{version.ToString(System.Globalization.CultureInfo.InvariantCulture)}\"";

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
    /// What an <c>If-Match</c> asserted, read without knowing the resource's
    /// current version.
    /// </summary>
    public enum Assertion
    {
        /// <summary>No <c>If-Match</c> header.</summary>
        Absent,

        /// <summary>The caller named one or more concrete versions.</summary>
        Versions,

        /// <summary><c>If-Match: *</c> — "whatever version, as long as it exists".</summary>
        AnyExisting,

        /// <summary>Present, but not a precondition this API can evaluate.</summary>
        Malformed,
    }

    /// <summary>
    /// Reads the versions an <c>If-Match</c> asserted, so a command can carry
    /// them to the handler that loads the row.
    /// </summary>
    /// <remarks>
    /// <see cref="Evaluate"/> answers the question a caller who already holds
    /// the current version can ask, and a controller following the sanctioned
    /// shape — <c>(await mediator.Send(command, ct)).ToActionResult()</c> — does
    /// not hold it: the row is loaded inside the handler. Without this overload
    /// the mechanism could only be used by a controller that queried the
    /// database first, which is the shape ADR-0032 § Sub-decision 6 exists to
    /// prevent.
    /// </remarks>
    public static Assertion ReadAssertion(HttpRequest request, out IReadOnlyList<long> versions)
    {
        ArgumentNullException.ThrowIfNull(request);

        versions = [];
        var raw = request.Headers.IfMatch;
        if (raw.Count == 0)
        {
            return Assertion.Absent;
        }

        if (!EntityTagHeaderValue.TryParseStrictList(raw, out var tags) || tags.Count == 0)
        {
            return Assertion.Malformed;
        }

        var asserted = new List<long>(tags.Count);

        foreach (var tag in tags)
        {
            if (tag.Equals(EntityTagHeaderValue.Any))
            {
                return Assertion.AnyExisting;
            }

            // Strong comparison: a weak tag says "semantically equivalent", and
            // two versions of a row that are semantically equivalent are still
            // two versions — one of which the client did not see.
            if (tag.IsWeak)
            {
                continue;
            }

            var quoted = tag.Tag.AsSpan();
            if (quoted.Length >= 2
                && long.TryParse(
                    quoted[1..^1],
                    System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var version))
            {
                asserted.Add(version);
            }
        }

        if (asserted.Count == 0)
        {
            return Assertion.Malformed;
        }

        versions = asserted;
        return Assertion.Versions;
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
    public static Precondition Evaluate(HttpRequest request, long currentVersion) =>
        ReadAssertion(request, out var versions) switch
        {
            Assertion.Absent => Precondition.Absent,
            Assertion.AnyExisting => Precondition.AnyExisting,
            Assertion.Malformed => Precondition.Failed,
            _ => versions.Contains(currentVersion) ? Precondition.Matched : Precondition.Failed,
        };

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
