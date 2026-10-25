using LearnStack.Application.Audit;
using LearnStack.Application.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

/// <summary>The sole production descriptor minter; facts alone never admit seed adoption.</summary>
public sealed class GlobalIdentityBindingService(
    IGlobalIdentityUnitOfWork unit,
    IAuditStateCapture capture,
    ITenantContext tenantContext,
    AuditExecutionCoordinator audit,
    TransactionExecutionCoordinator transaction,
    IdentityAuthorityQualification qualification,
    ISeedIdentityFoundationAdmission seedAdmission,
    Func<IUserBindingStore> store,
    Func<CreateUserBindingHandler> handler)
    : GlobalIdentityOwnerDescriptor.MintingSource, IIdentityBindingReader, ISeedIdentityBindingAdopter
{
    public async Task<IdentityBindingResult> ResolveExistingAsync(GlobalIdentitySubject subject, UserId? claimedActor = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (HasInheritedAuthority()) return Refused(IdentityBindingOutcome.AdmissionDenied);
        if (claimedActor is { } claimed && !ValidActor(claimed)) return Refused(IdentityBindingOutcome.Conflict);
        if (qualification.Current?.Matches(subject) is not true) return Refused(IdentityBindingOutcome.AuthorityUnavailable);
        var descriptor = ForLookup(subject, typeof(IdentityDbContext), typeof(User));
        try
        {
            await using var frame = await unit.BeginGlobalIdentityTransactionAsync(descriptor, TransactionMode.ReadOnly, cancellationToken);
            var result = Resolve(await store().FindExactAsync(subject, cancellationToken), subject, claimedActor);
            await frame.CompleteAsync(cancellationToken);
            return result;
        }
        catch (NpgsqlException) { return Refused(IdentityBindingOutcome.StorageUnavailable); }
        catch (TimeoutException) { return Refused(IdentityBindingOutcome.StorageUnavailable); }
    }

    public async Task<IdentityBindingResult> AdoptAsync(GlobalIdentitySubject subject, UserId declaredActor,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subject);
        if (HasInheritedAuthority()) return Refused(IdentityBindingOutcome.AdmissionDenied);
        if (!ValidActor(declaredActor)) return Refused(IdentityBindingOutcome.Conflict);
        if (!await seedAdmission.AllowsAsync(subject, declaredActor, cancellationToken))
            return Refused(IdentityBindingOutcome.AdmissionDenied);
        if (qualification.Current?.Matches(subject) is not true) return Refused(IdentityBindingOutcome.AuthorityUnavailable);
        var descriptor = ForSeedAdoption(subject, declaredActor, typeof(IdentityDbContext), typeof(User));
        TransactionExecutionCoordinator.PreparedGlobalIdentityOwner? owner = null;
        try
        {
            owner = await TransactionExecutionCoordinator.PrepareGlobalIdentityOwnerAsync(
                unit, capture, tenantContext, descriptor, cancellationToken);
            var current = await store().FindExactAsync(subject, cancellationToken);
            if (current is not null)
            {
                var result = Resolve(current, subject, declaredActor);
                await owner.CompleteExistingAsync(cancellationToken);
                return result;
            }
            owner.AdmitSerializedAbsence();
            var response = await audit.ExecuteGlobalIdentityAsync(owner,
                reference => transaction.ExecutePreparedGlobalIdentityAsync(owner, reference,
                    () => handler().HandleAsync(subject, reference, cancellationToken), cancellationToken), cancellationToken);
            return response.IsSuccess
                ? IdentityBindingResult.Bound(response.Value, created: true)
                : Refused(IdentityBindingOutcome.StorageUnavailable);
        }
        catch (NpgsqlException) { return StorageFailure(owner); }
        catch (AuditWriteFailedException) { return StorageFailure(owner); }
        catch (UserBindingStorageException) { return StorageFailure(owner); }
        catch (IOException) { return StorageFailure(owner); }
        catch (TimeoutException) { return StorageFailure(owner); }
        finally
        {
            if (owner is not null) await owner.DisposeAsync();
        }
    }

    private static IdentityBindingResult StorageFailure(TransactionExecutionCoordinator.PreparedGlobalIdentityOwner? owner) =>
        Refused(owner?.RecordedOutcome == AuditIntentState.Indeterminate
            ? IdentityBindingOutcome.Indeterminate : IdentityBindingOutcome.StorageUnavailable);

    private bool HasInheritedAuthority() => tenantContext.IsResolved || tenantContext is EventTenantContext or JobTenantContext
        || unit.HasActiveTransaction || unit.IsRollbackOnly || capture.HasOpenFrame || capture.Intents.Count != 0
        || capture.Changes.Count != 0 || capture.State != AuditIntentState.None;

    private static IdentityBindingResult Resolve(User? user, GlobalIdentitySubject subject, UserId? claimedActor)
    {
        if (user is null) return Refused(IdentityBindingOutcome.NotFound);
        if (user.Status != UserStatus.Active) return Refused(IdentityBindingOutcome.NonAdmitting);
        if (user.AuthorityId != subject.AuthorityId || !user.SubjectKey.Equals(subject.SubjectKey)
            || !string.Equals(user.ProviderSubject, subject.Subject, StringComparison.Ordinal)
            || (claimedActor is { } actor && actor != user.Id)) return Refused(IdentityBindingOutcome.Conflict);
        return IdentityBindingResult.Bound(user.Id, created: false);
    }

    private static bool ValidActor(UserId actor) => actor.IsInitialized() && actor.Value != Guid.Empty
        && actor.Value.Version == 7 && actor != UserId.SystemActor;

    private static IdentityBindingResult Refused(IdentityBindingOutcome outcome) => IdentityBindingResult.Refused(outcome);
}
