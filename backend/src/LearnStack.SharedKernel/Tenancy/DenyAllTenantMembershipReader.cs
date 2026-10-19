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
/// reconciliation matrix's rows 7, 10 and 14 fail closed, and the Studio tenant switcher
/// returns <b>404 <c>not_found</c></b> for everyone in that window.
/// </para>
/// <para>
/// <b>The API reaches this reader after P02b-5 authentication.</b> Validated
/// claims drive rows 7, 10 and 14, and each still refuses until Phase 03 supplies
/// active memberships. Matching host-and-claim rows 6 and 9 need no membership read.
/// </para>
/// <para>
/// <b>Denying is not the same as throwing.</b> A reader that threw would make a
/// reachable membership question an outage; a reader
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
