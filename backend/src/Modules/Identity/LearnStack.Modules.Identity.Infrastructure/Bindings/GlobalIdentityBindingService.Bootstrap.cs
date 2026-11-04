using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

public sealed partial class GlobalIdentityBindingService
{
    public async Task<IdentityBootstrapResult> BootstrapAsync(GlobalIdentitySubject subject, UserId? claimedActor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (HasInheritedAuthority() || !selfAdmission.Allows(subject, IdentitySelfOperation.Bootstrap, claimedActor))
            return IdentityBootstrapResult.FromBinding(Refused(IdentityBindingOutcome.AdmissionDenied));
        var initial = await ResolveExistingAsync(subject, claimedActor, cancellationToken);
        if (initial.Outcome != IdentityBindingOutcome.NotFound) return IdentityBootstrapResult.FromBinding(initial);
        if (claimedActor is not null) return IdentityBootstrapResult.FromBinding(Refused(IdentityBindingOutcome.Conflict));
        if (!selfAdmission.CreationEnabled) return IdentityBootstrapResult.Refused(IdentityCreationRefusal.Disabled);
        if (!selfAdmission.Allows(subject, IdentitySelfOperation.Bootstrap, claimedActor))
            return IdentityBootstrapResult.FromBinding(Refused(IdentityBindingOutcome.AdmissionDenied));

        var refusal = await DebitAttemptAsync(subject, cancellationToken);
        if (refusal is not null) return refusal;
        cancellationToken.ThrowIfCancellationRequested();
        if (!selfAdmission.Allows(subject, IdentitySelfOperation.Bootstrap, claimedActor))
            return IdentityBootstrapResult.FromBinding(Refused(IdentityBindingOutcome.AdmissionDenied));
        return IdentityBootstrapResult.FromBinding(await CreateAsync(subject,
            ForBootstrap(subject, typeof(IdentityDbContext), typeof(User)), null, cancellationToken));
    }

    // Null means an acknowledged committed debit, not merely a successful SQL
    // return. A failed/lost acknowledgement never starts creation or refunds it.
    private async Task<IdentityBootstrapResult?> DebitAttemptAsync(GlobalIdentitySubject subject, CancellationToken cancellationToken)
    {
        var descriptor = ForCreationAttempt(subject, typeof(IdentityDbContext), typeof(User));
        IUnitOfWorkScope? frame = null;
        IdentityBootstrapResult? result;
        bool cleanupSucceeded;
        try
        {
            frame = await unit.BeginGlobalIdentityTransactionAsync(descriptor, TransactionMode.ReadWrite, cancellationToken);
            await unit.AcquireGlobalIdentitySubjectLockAsync(descriptor, cancellationToken);
            var existing = await store().FindExactAsync(subject, cancellationToken);
            if (existing is not null) result = IdentityBootstrapResult.FromBinding(Resolve(existing, subject, null));
            else
            {
                result = await ExecuteDebitAsync(descriptor, cancellationToken);
            }
            await unit.ValidateGlobalIdentityConstraintsAsync(descriptor, cancellationToken);
            await frame.CompleteAsync(cancellationToken);
        }
        catch (NpgsqlException) { result = IdentityBootstrapResult.Refused(IdentityCreationRefusal.Unavailable); }
        catch (UserBindingStorageException) { result = IdentityBootstrapResult.Refused(IdentityCreationRefusal.Unavailable); }
        catch (TimeoutException) { result = IdentityBootstrapResult.Refused(IdentityCreationRefusal.Unavailable); }
        finally { cleanupSucceeded = await DisposeOwnerAsync(frame); }
        return cleanupSucceeded ? result : IdentityBootstrapResult.Refused(IdentityCreationRefusal.Unavailable);
    }

    private async Task<IdentityBootstrapResult?> ExecuteDebitAsync(GlobalIdentityOwnerDescriptor descriptor,
        CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(unit.GetGlobalIdentityAnnouncement(unit.Transaction)?.Descriptor, descriptor))
            throw new InvalidOperationException("Creation debit requires its exact global owner.");
        await using var command = new NpgsqlCommand(
            "SELECT admitted,retry_after_seconds FROM public.fn_identity_creation_attempt_admission()",
            (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) throw new UserBindingStorageException();
        var admitted = reader.GetBoolean(0);
        int? retry = reader.IsDBNull(1) ? null : reader.GetInt32(1);
        if (await reader.ReadAsync(cancellationToken) || (admitted ? retry is not null : retry is not (>= 1 and <= 86400)))
            throw new UserBindingStorageException();
        return admitted ? null : IdentityBootstrapResult.Refused(IdentityCreationRefusal.WindowExhausted, retry);
    }
}
