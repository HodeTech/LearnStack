using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Api.Authentication;

/// <summary>
/// Binds a download ticket to the live session that minted it (ADR-0107 § 4): a SHA-256 digest, domain-separated and
/// length-prefixed, of the announced group, the actor, the verified issuer and subject, and the digest of the exact
/// bearer credential this request presented and the tenant scheme validated.
/// </summary>
/// <remarks>
/// <para>
/// Nothing a caller chooses selects it: the credential feature is set only by the stock token validation of this
/// request, and the actor must be the one its binding normalized. A request without a validated tenant credential,
/// with a second <c>Authorization</c> value, or for another actor gets no binding, so no ticket is minted or honoured.
/// </para>
/// <para>
/// The raw credential never leaves this method; only its digest enters the binding, and the binding is itself only
/// a digest on the ticket row, which the audit capture redacts.
/// </para>
/// <para>
/// <b>The retained own-scope entry (ADR-0106 § 9) binds under its own domain.</b> There the binding is captured only
/// while the request's <see cref="OwnDataRequestSubjectSource"/> is current on a ticket or chunk purpose for exactly
/// this actor and group, from the digest of the exact <c>Authorization</c> header its credential capture recorded at
/// validation. The separate domain means a ticket minted on one path is never honoured on the other, even for the
/// same person, group and credential. As on the live path the native credential is the session: when the BFF's entry
/// handle refreshes it the binding no longer matches, that download's cursor ends and the resumed download mints a
/// new ticket.
/// </para>
/// </remarks>
internal sealed class HttpDataExportSessionBinding(IHttpContextAccessor accessor) : IDataExportSessionBinding
{
    private const string Domain = "learnstack.data_export.session.v1";
    private const string RetainedDomain = "learnstack.data_export.retained_session.v1";
    private const string Prefix = "Bearer ";

    public string? Capture(UserId actor, TenantId tenant, OrganizationId? organization)
    {
        var http = accessor.HttpContext;
        if (http is not null && (http.Features.Get<OwnDataRequestSubjectSource>() is not null
            || http.Features.Get<NativeSessionSourceFeature>()?.DataRequestOperation is not null))
        {
            return CaptureRetained(http, actor, tenant, organization);
        }

        if (http is null || http.RequestAborted.IsCancellationRequested
            || http.Features.Get<TenantCredentialFeature>() is not { VerifiedSubject: { } subject, Identity: { } identity }
            || identity.ActorUserId != actor || actor == UserId.SystemActor
            || http.Request.Headers.Authorization is not { Count: 1 } values || values[0] is not { } header
            || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || header.Length <= Prefix.Length)
        {
            return null;
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(digest, Domain);
        Append(digest, tenant.Value.ToString("D"));
        Append(digest, organization?.Value.ToString("D") ?? string.Empty);
        Append(digest, actor.Value.ToString("D"));
        Append(digest, subject.Issuer);
        Append(digest, subject.Subject);
        Append(digest, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(header[Prefix.Length..]))));
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static string? CaptureRetained(HttpContext http, UserId actor, TenantId tenant, OrganizationId? organization)
    {
        if (http.RequestAborted.IsCancellationRequested || actor == UserId.SystemActor
            || http.Features.Get<OwnDataRequestSubjectSource>() is not { } source
            || source.DownloadCredential(http, actor, tenant, organization) is not { } credential
            || http.Features.Get<TenantCredentialFeature>() is not { VerifiedSubject: { } subject, Identity: { } identity }
            || identity.ActorUserId != actor || !ReferenceEquals(credential.Subject, subject))
        {
            return null;
        }

        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(digest, RetainedDomain);
        Append(digest, tenant.Value.ToString("D"));
        Append(digest, organization?.Value.ToString("D") ?? string.Empty);
        Append(digest, actor.Value.ToString("D"));
        Append(digest, subject.Issuer);
        Append(digest, subject.Subject);
        Append(digest, credential.CredentialDigest);
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static void Append(IncrementalHash digest, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
        digest.AppendData(length);
        digest.AppendData(bytes);
    }

    public override string ToString() => nameof(HttpDataExportSessionBinding);
}
