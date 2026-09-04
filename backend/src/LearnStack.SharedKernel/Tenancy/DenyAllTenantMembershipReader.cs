using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// The <see cref="ITenantMembershipReader"/> that covers nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is correct, and it will look like a bug.</b> There is no
/// <c>Membership</c> aggregate until
/// <see href="../../../../docs/roadmap/phase-03-identity-admin.md">Phase 03</see>,
/// so there is nothing to read and the only honest answer to "does an active
/// membership cover this?" is <c>false</c>. The consequence is named here with its
/// error code so that nobody makes the default permissive to unblock a demo: the
/// reconciliation matrix's rows 7 and 14 fail closed, and the Studio tenant switcher
/// returns <b>404 <c>not_found</c></b> for everyone in that window.
/// </para>
/// <para>
/// <b>Nothing can reach it in Packet 7 either.</b> Rows 7, 10 and 14 all require a
/// validated claim, and there is no <c>UseAuthentication</c> until Phase 02b. So the
/// port is registered and the factory consults it, and no request can produce the
/// claim that would make the call happen. That is stated rather than dressed up,
/// because a reader who believes this path is exercised will draw the wrong
/// conclusion from a green suite.
/// </para>
/// <para>
/// <b>Denying is not the same as throwing.</b> A reader that threw would make an
/// unreachable path an outage the first time Phase 02b made it reachable; a reader
/// that denies makes it a refusal, which is what the matrix specifies and what
/// Phase 03 turns into an answer.
/// </para>
/// </remarks>
public sealed class DenyAllTenantMembershipReader : ITenantMembershipReader
{
    /// <inheritdoc />
    public Task<bool> CoversAsync(
        UserId userId,
        TenantId tenantId,
        OrganizationId? organizationId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}
