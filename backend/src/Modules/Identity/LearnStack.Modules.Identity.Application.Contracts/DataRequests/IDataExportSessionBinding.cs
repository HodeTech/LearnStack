using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Contracts.DataRequests;

/// <summary>
/// The session a download ticket is bound to
/// (<see href="../../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 4</see>).
/// </summary>
/// <remarks>
/// <para>
/// The API sees no session identifier of its own: a live session reaches it as the stock-validated bearer credential
/// the BFF presents for that session. The binding is therefore a SHA-256 digest of that exact presented credential
/// together with the actor and the announced group, computed by the transport that validated it. A ticket minted in
/// one session is refused in another, and a ticket alone is not a bearer credential. A credential the BFF refreshes
/// mid-download ends that download's cursor; the resumed download mints a new ticket, as ADR-0107 § 4 requires anyway.
/// </para>
/// <para>
/// The default registration answers nothing, so outside the HTTP composition no ticket can be minted or used.
/// </para>
/// </remarks>
public interface IDataExportSessionBinding
{
    /// <summary>
    /// The lower-case hex SHA-256 binding of the current request's validated credential for exactly this actor and
    /// group, or <see langword="null"/> when the current request carries none.
    /// </summary>
    string? Capture(UserId actor, TenantId tenant, OrganizationId? organization);
}

/// <summary>The fail-closed default: no session, so no ticket is minted or honoured.</summary>
public sealed class UnavailableDataExportSessionBinding : IDataExportSessionBinding
{
    public string? Capture(UserId actor, TenantId tenant, OrganizationId? organization) => null;
}
