using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// The group check every <see cref="IUserDataParticipant"/> makes before it reads: a participant reads only the
/// announced group of its request
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1</see>).
/// </summary>
public static class UserDataParticipantGuard
{
    /// <summary>
    /// Refuses a subject whose group is not the ambient context's tenant and organization. The job announces the
    /// request's group before it calls a participant, so a mismatch is a programming error, never a refusal.
    /// </summary>
    /// <exception cref="InvalidOperationException">The subject's group is not the announced group.</exception>
    public static void RequireGroup(UserDataSubject subject, ITenantContext tenant)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(tenant);
        if (subject.Group.TenantId != tenant.TenantId || subject.Group.OrganizationId != tenant.OrganizationId)
        {
            throw new InvalidOperationException("A participant reads only the announced group of its request.");
        }
    }
}
