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
using Microsoft.Extensions.Logging;
using Npgsql;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

/// <summary>The sole production descriptor minter; facts alone never admit seed adoption.</summary>
public sealed class GlobalIdentityBindingService(
    IGlobalIdentityUnitOfWork unit,
    IAuditStateCapture capture,
    ITenantContextAccessor tenantAccessor,
    Func<AuditExecutionCoordinator> audit,
    Func<TransactionExecutionCoordinator> transaction,
    IdentityAuthorityQualification qualification,
    ISeedIdentityFoundationAdmission seedAdmission,
    Func<IUserBindingStore> store,
    Func<CreateUserBindingHandler> handler,
    ILogger<GlobalIdentityBindingService> logger)
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
        IUnitOfWorkScope? frame = null;
        IdentityBindingResult result;
        bool cleanupSucceeded;
        try
        {
            frame = await unit.BeginGlobalIdentityTransactionAsync(descriptor, TransactionMode.ReadOnly, cancellationToken);
            result = Resolve(await store().FindExactAsync(subject, cancellationToken), subject, claimedActor);
            await frame.CompleteAsync(cancellationToken);
        }
        catch (NpgsqlException) { result = Refused(IdentityBindingOutcome.StorageUnavailable); }
        catch (TimeoutException) { result = Refused(IdentityBindingOutcome.StorageUnavailable); }
        finally { cleanupSucceeded = await DisposeOwnerAsync(frame); }
        return cleanupSucceeded ? result : Refused(IdentityBindingOutcome.StorageUnavailable);
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
        IdentityBindingResult result;
        bool cleanupSucceeded;
        try
        {
            owner = await TransactionExecutionCoordinator.PrepareGlobalIdentityOwnerAsync(
                unit, capture, CurrentTenantContext, descriptor, cancellationToken);
            var current = await store().FindExactAsync(subject, cancellationToken);
            if (current is not null)
            {
                result = Resolve(current, subject, declaredActor);
                await owner.CompleteExistingAsync(cancellationToken);
            }
            else
            {
                owner.AdmitSerializedAbsence();
                var response = await audit().ExecuteGlobalIdentityAsync(owner,
                    reference => transaction().ExecutePreparedGlobalIdentityAsync(owner, reference,
                        () => handler().HandleAsync(subject, reference, cancellationToken), cancellationToken), cancellationToken);
                result = response.IsSuccess
                    ? IdentityBindingResult.Bound(response.Value, created: true)
                    : Refused(IdentityBindingOutcome.StorageUnavailable);
            }
        }
        catch (NpgsqlException) { result = StorageFailure(owner); }
        catch (AuditWriteFailedException) { result = StorageFailure(owner); }
        catch (UserBindingStorageException) { result = StorageFailure(owner); }
        catch (IOException) { result = StorageFailure(owner); }
        catch (TimeoutException) { result = StorageFailure(owner); }
        finally { cleanupSucceeded = await DisposeOwnerAsync(owner); }
        return cleanupSucceeded ? result : StorageFailure(owner);
    }

    private static IdentityBindingResult StorageFailure(TransactionExecutionCoordinator.PreparedGlobalIdentityOwner? owner) =>
        Refused(owner?.RecordedOutcome == AuditIntentState.Indeterminate
            ? IdentityBindingOutcome.Indeterminate : IdentityBindingOutcome.StorageUnavailable);

    private ITenantContext CurrentTenantContext => tenantAccessor.Current ?? UnresolvedTenantContext.Instance;

    private bool HasInheritedAuthority()
    {
        var context = CurrentTenantContext;
        return context.IsResolved || context is EventTenantContext or JobTenantContext
            || unit.HasActiveTransaction || unit.IsRollbackOnly || capture.HasOpenFrame || capture.Intents.Count != 0
            || capture.Changes.Count != 0 || capture.State != AuditIntentState.None;
    }

    private async ValueTask<bool> DisposeOwnerAsync(IAsyncDisposable? owner)
    {
        if (owner is null) return true;
        try
        {
            await owner.DisposeAsync();
            return true;
        }
#pragma warning disable CA1031 // Cleanup is awaited without replacing a primary failure or cancellation.
        catch (Exception)
#pragma warning restore CA1031
        {
            LogCleanupFailure(logger, null);
            return false;
        }
    }

    private static readonly Action<ILogger, Exception?> LogCleanupFailure = LoggerMessage.Define(
        LogLevel.Error, new EventId(1, "GlobalIdentityOwnerCleanupFailed"),
        "Global identity owner cleanup failed; its scope retains connection cleanup responsibility.");

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
