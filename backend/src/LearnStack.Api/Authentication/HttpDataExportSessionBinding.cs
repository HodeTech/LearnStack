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
/// </remarks>
internal sealed class HttpDataExportSessionBinding(IHttpContextAccessor accessor) : IDataExportSessionBinding
{
    private const string Domain = "learnstack.data_export.session.v1";
    private const string Prefix = "Bearer ";

    public string? Capture(UserId actor, TenantId tenant, OrganizationId? organization)
    {
        var http = accessor.HttpContext;
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
