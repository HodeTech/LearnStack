using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Infrastructure.DataProtection;

/// <summary>
/// The shape of a module whose rows are tenant data, not the person's: an empty export section and one retained kind,
/// <c>attribution_reference</c>, counted over the module's own context
/// (<see href="../../../../docs/decisions/0107-data-request-fulfillment-and-account-closure.md">ADR-0107 § 1, § 3, § 6</see>).
/// </summary>
/// <remarks>
/// A base, not a participant: each module derives its own sealed class inside its boundary and registers it there,
/// so the participant census still names one participant per module and no module references another's. Erasure is
/// the port's default no-op.
/// </remarks>
public abstract class DescribeOnlyUserDataParticipant(DbContext db, ITenantContext tenant) : IUserDataParticipant
{
    public abstract string Key { get; }

    /// <summary>Writes nothing: the module's rows are tenant data and the export excludes them (ADR-0107 § 3).</summary>
    public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(writer);
        UserDataParticipantGuard.RequireGroup(subject, tenant);
        return Task.CompletedTask;
    }

    /// <summary>The rows of the module's context whose <c>UserId</c> columns name the subject, once per row.</summary>
    public async Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(subject);
        UserDataParticipantGuard.RequireGroup(subject, tenant);
        return
        [
            new(RetainedReferenceKind.AttributionReference,
                await AttributionReferenceCounter.CountAsync(db, subject, null, cancellationToken).ConfigureAwait(false)),
        ];
    }
}
