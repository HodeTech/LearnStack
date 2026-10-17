using LearnStack.Application.Jobs;
using System.Data.Common;
using LearnStack.Application.Messaging;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;

namespace LearnStack.Application.Persistence;

/// <summary>
/// Shared transaction entry and owner completion for requests and direct work.
/// The outer audit coordinator retains classification/reconciliation ownership.
/// </summary>
public sealed class TransactionExecutionCoordinator(
    IUnitOfWork unitOfWork,
    ITenantContext tenantContext,
    IAuditStore auditStore,
    IAuditStateCapture capture,
    ProducerExecution producer,
    DomainEventCollector collector,
    ILogger<TransactionExecutionCoordinator> logger)
{
    /// <summary>
    /// Announces before dispatch, retains producer ancestry through completion,
    /// and reports the actual durability boundary (ADR-0057).
    /// </summary>
    internal Task<TResponse> ExecuteRequestAsync<TRequest, TResponse>(TRequest request,
        TransactionMode mode, Func<Task<TResponse>> next, CancellationToken cancellationToken = default)
        where TRequest : notnull where TResponse : IResultBase =>
        ExecuteCoreAsync(request as IProvisionsTenant, mode, typeof(TRequest).Name, next,
            typeof(TRequest), request as AdmitBackgroundJobAttemptCommand,
            request as RecordBackgroundJobCapacityCleanupCommand, cancellationToken);

    public Task<TResponse> ExecuteAsync<TResponse>(IProvisionsTenant? provisioning, TransactionMode mode,
        string operationName, Func<Task<TResponse>> next, CancellationToken cancellationToken = default)
        where TResponse : IResultBase => ExecuteCoreAsync(provisioning, mode, operationName, next, null, null, null, cancellationToken);

    private async Task<TResponse> ExecuteCoreAsync<TResponse>(
        IProvisionsTenant? provisioning,
        TransactionMode mode,
        string operationName,
        Func<Task<TResponse>> next, Type? operationType, AdmitBackgroundJobAttemptCommand? admission,
        RecordBackgroundJobCapacityCleanupCommand? capacityCleanup,
        CancellationToken cancellationToken)
        where TResponse : IResultBase
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        BackgroundJobExecutionAdmission.RequireContext(tenantContext, provisioning);
        BackgroundJobExecutionAdmission.RequireOperation(operationType);
        var jobCompletion = BackgroundJobExecutionAdmission.RequireOwnerEntry(unitOfWork, mode, operationType, tenantContext);
        var openingOwner = !unitOfWork.HasActiveTransaction;
        IUnitOfWorkScope scope;
        try
        {
            if (openingOwner) collector.RefreshTrackedSources();
            scope = await unitOfWork.BeginTransactionAsync(mode, cancellationToken);
        }
        catch
        {
            if (openingOwner)
            {
                try
                {
                    collector.Clear();
                }
                catch
                {
                    // Retain the setup failure; the DI scope still owns cleanup.
                }
            }

            throw;
        }

        return await ExecuteFrameAsync(scope, provisioning, operationName, next,
            null, null, null, operationType, admission, jobCompletion, capacityCleanup, cancellationToken);
    }

    /// <summary>
    /// Opens only the delivery's routing transaction, before full envelope metadata or
    /// module services exist. The private token retains the actual owner frame.
    /// </summary>
    public static Task<PreparedDeliveryOwner> PrepareDeliveryOwnerAsync(
        IUnitOfWork unitOfWork, ITenantContext routingContext,
        CancellationToken cancellationToken = default)
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        return PreparedDeliveryOwner.CreateAsync(unitOfWork, routingContext, cancellationToken);
    }

    /// <summary>Consumes the prepared owner without joining or announcing a second frame.</summary>
    public Task<TResponse> ExecutePreparedDeliveryAsync<TResponse>(
        PreparedDeliveryOwner owner,
        DeliveryCompletionParticipant completion,
        Guid incomingEventId,
        Func<Task<TResponse>> next,
        Type? operationType = null, CancellationToken cancellationToken = default)
        where TResponse : IResultBase
    {
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(completion);
        ArgumentNullException.ThrowIfNull(next);
        if (incomingEventId == Guid.Empty)
        {
            throw new ArgumentException("Delivery production requires its incoming event identity.", nameof(incomingEventId));
        }

        completion.RequireUnit(unitOfWork);
        var frame = owner.Consume(unitOfWork, tenantContext);
        return ExecuteFrameAsync(frame, null, "delivery", next, owner, completion,
            incomingEventId, operationType, null, null, null, cancellationToken);
    }

    private async Task<TResponse> ExecuteFrameAsync<TResponse>(
        IUnitOfWorkScope scope,
        IProvisionsTenant? provisioning,
        string operationName,
        Func<Task<TResponse>> next,
        PreparedDeliveryOwner? preparedOwner,
        DeliveryCompletionParticipant? completion,
        Guid? incomingEventId, Type? operationType, AdmitBackgroundJobAttemptCommand? admission,
        BackgroundJobCompletionParticipant? jobCompletion,
        RecordBackgroundJobCapacityCleanupCommand? capacityCleanup,
        CancellationToken cancellationToken)
        where TResponse : IResultBase
    {
        var committing = false;
        var failed = false;

        try
        {
            BackgroundJobExecutionAdmission.RequireContext(tenantContext, provisioning);
            // Provisioning changes SQL authority only; the ambient accessor stays
            // unresolved. A resolved caller cannot announce another tenant here.
            if (preparedOwner is not null)
            {
                preparedOwner.RequireLive(unitOfWork, tenantContext);
                collector.RefreshTrackedSources();
            }
            else if (!tenantContext.IsResolved && provisioning is not null)
            {
                await unitOfWork.SetProvisioningTenantContextAsync(
                    provisioning.ProvisioningTenantId, cancellationToken);
            }
            else
            {
                await unitOfWork.SetTenantContextAsync(
                    BackgroundJobExecutionAdmission.AnnouncementContext(tenantContext, operationType), cancellationToken);
            }

            // Capture causal identity at the trusted entry. Nested invocations inherit it;
            // asynchronous work keeps SystemActor effective without impersonating a human.
            var causalActor = tenantContext.IsResolved
                ? tenantContext.CausalActorUserId
                    ?? (tenantContext.UserId == UserId.SystemActor ? null : tenantContext.UserId)
                : null;
            admission?.BindOwner(unitOfWork, scope.IsOwner);
            capacityCleanup?.BindOwner(unitOfWork, scope.IsOwner);
            if (jobCompletion is not null)
                await BackgroundJobExecutionAdmission.BindOwnerAsync(jobCompletion, unitOfWork, scope.IsOwner,
                    cancellationToken).ConfigureAwait(false);
            using var invocation = producer.BeginInvocation(scope.IsOwner, causalActor,
                incomingEventId ?? BackgroundJobExecutionAdmission.IncomingEventId, operationType,
                BackgroundJobExecutionAdmission.ParentJobId);
            var response = await next();
            if (response.IsFailure)
            {
                // Joiners are not savepoints. Successful production cannot escape
                // merely because an ancestor absorbs this refusal.
                if (invocation.HasProducedWork) unitOfWork.MarkRollbackOnly();
                await scope.FailAsync(CancellationToken.None);
                jobCompletion?.Gate.RecordRolledBack();
                capacityCleanup?.RecordRolledBack();
                if (scope.IsOwner) capture.MarkRolledBack();
                return response;
            }

            if (scope.IsOwner)
            {
                // Refuse before MUST composition. Refused commit is known rollback;
                // a failed COMMIT acknowledgement has an indeterminate outcome.
                if (unitOfWork.IsRollbackOnly
                    || (unitOfWork.Mode == TransactionMode.ReadOnly && invocation.HasProducedWork))
                {
                    throw new InvalidOperationException(
                        "The transaction is rollback-only or contains produced work in read-only mode.");
                }

                await collector.DrainAsync(invocation, cancellationToken);
                if (invocation.HasProducedWork) _ = producer.RequireAnnouncement();
                await auditStore.WritePendingAsync(unitOfWork, cancellationToken);
            }

            if (completion is not null)
            {
                preparedOwner!.RequireLive(unitOfWork, tenantContext);
                await completion.FenceOwnerAsync(unitOfWork, cancellationToken);
                preparedOwner.RequireLive(unitOfWork, tenantContext);
                if (!completion.Gate.TryBeginCommit())
                {
                    throw new OperationCanceledException("Delivery commit admission is closed.", cancellationToken);
                }
            }

            if (jobCompletion is not null)
            {
                if (!scope.IsOwner || completion is not null || preparedOwner is not null)
                    throw new InvalidOperationException("Job completion belongs only to its ordinary root owner.");
                await BackgroundJobExecutionAdmission.FenceOwnerAsync(jobCompletion, unitOfWork,
                    cancellationToken).ConfigureAwait(false);
                if (!jobCompletion.Gate.TryBeginCommit())
                    throw new OperationCanceledException("Job commit admission is closed.", cancellationToken);
            }

            // No await between the atomic admission above and the owned Complete call.
            admission?.MarkCommitting(unitOfWork);
            capacityCleanup?.MarkCommitting(unitOfWork);
            committing = true;
            try
            {
                await scope.CompleteAsync(cancellationToken);
            }
            catch (Exception commitFailure)
            {
                if (unitOfWork.HasActiveTransaction)
                {
                    // The adapter detaches its transaction before physical COMMIT.
                    // A live frame therefore refused completion before COMMIT (for
                    // example, a leaked joiner). Use the guarded rollback path,
                    // preserving the frame error even if cleanup also fails.
                    committing = false;
                }
                else if (scope.IsOwner)
                {
                    if (unitOfWork.IsRollbackOnly) capture.MarkRolledBack();
                    else capture.MarkIndeterminate(commitFailure);
                }

                throw;
            }

            if (scope.IsOwner)
            {
                jobCompletion?.Gate.RecordCommitted();
                admission?.RecordCommitted(unitOfWork);
                capacityCleanup?.RecordCommitted(unitOfWork);
                completion?.Gate.RecordCommitted();
                capture.MarkCommitted();
            }
            return response;
        }
        catch when (!committing)
        {
            failed = true;
            unitOfWork.MarkRollbackOnly();
            // Cleanup must neither be abandoned by cancellation nor replace the
            // original dispatch/audit failure with a bookkeeping exception.
            try
            {
                await scope.FailAsync(CancellationToken.None);
                jobCompletion?.Gate.RecordRolledBack();
                capacityCleanup?.RecordRolledBack();
            }
            catch (Exception rollbackFailure)
            {
                LogRollbackFailure(logger, operationName,
                    AsyncExecutionDiagnostics.IsTrusted(tenantContext) ? null : rollbackFailure);
            }

            if (scope.IsOwner && capture.State != AuditIntentState.Indeterminate)
            {
                capture.MarkRolledBack();
            }

            throw;
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            await CleanupFrameAsync(scope, failed);
        }
    }

    private async ValueTask CleanupFrameAsync(IUnitOfWorkScope scope, bool failed)
    {
        // Announcement/setup failures also reach here. Child completion never
        // drains or clears sources belonging to the still-open physical owner.
        try
        {
            if (scope.IsOwner) collector.Clear();
        }
        catch when (failed)
        {
            // A source's cleanup cannot replace the primary failure.
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            try
            {
                await scope.DisposeAsync();
            }
            catch when (failed)
            {
                // Preserve the original dispatch/COMMIT/cleanup exception.
                // Commit acknowledgment predates cleanup and remains definitive.
            }
        }
    }

    /// <summary>
    /// A single-use delivery owner, minted only by the closed preparation entry. An
    /// unconsumed token rolls back, including failures before audit classification.
    /// </summary>
#pragma warning disable CA1034 // Nesting keeps prepared owner construction private without friend assemblies.
    public sealed class PreparedDeliveryOwner : IAsyncDisposable
#pragma warning restore CA1034
    {
        private readonly IUnitOfWork _unit;
        private readonly IUnitOfWorkScope _frame;
        private readonly DbTransaction _transaction;
        private readonly TenantContextAnnouncement _announcement;
        private bool _consumed;
        private bool _disposed;

        private PreparedDeliveryOwner(IUnitOfWork unit, IUnitOfWorkScope frame,
            DbTransaction transaction, TenantContextAnnouncement announcement)
        {
            _unit = unit;
            _frame = frame;
            _transaction = transaction;
            _announcement = announcement;
        }

        internal static async Task<PreparedDeliveryOwner> CreateAsync(
            IUnitOfWork unit, ITenantContext routingContext, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(unit);
            ArgumentNullException.ThrowIfNull(routingContext);
            cancellationToken.ThrowIfCancellationRequested();
            if (unit.HasActiveTransaction || unit.IsRollbackOnly || !routingContext.IsResolved
                || !routingContext.TenantId.IsInitialized()
                || routingContext.TenantId.Value == Guid.Empty
                || routingContext.TenantId == TenantId.PlatformSentinel
                || (routingContext.OrganizationId is { } organization
                    && (!organization.IsInitialized() || organization.Value == Guid.Empty)))
            {
                throw new InvalidOperationException("Delivery preparation requires a fresh unit and valid routing scope.");
            }

            var frame = await unit.BeginTransactionAsync(TransactionMode.ReadWrite, cancellationToken);
            try
            {
                if (!frame.IsOwner)
                {
                    throw new InvalidOperationException("Delivery preparation must own its transaction.");
                }

                await unit.SetTenantContextAsync(routingContext, cancellationToken);
                var transaction = unit.Transaction
                    ?? throw new InvalidOperationException("Delivery preparation lost its transaction.");
                var announcement = unit.GetTenantContextAnnouncement(transaction)
                    ?? throw new InvalidOperationException("Delivery preparation requires an announced transaction.");
                var owner = new PreparedDeliveryOwner(unit, frame, transaction, announcement);
                owner.RequireLive(unit, routingContext);
                return owner;
            }
            catch
            {
                unit.MarkRollbackOnly();
                try
                {
                    await frame.FailAsync(CancellationToken.None);
                }
                catch
                {
                    // Preserve preparation failure, including cancellation.
                }

                try
                {
                    await frame.DisposeAsync();
                }
                catch
                {
                    // The fresh DI scope still owns connection cleanup.
                }
                throw;
            }
        }

        internal void RequireLive(IUnitOfWork unit, ITenantContext context)
        {
            if (_disposed || !ReferenceEquals(_unit, unit) || !_frame.IsOwner
                || !unit.HasActiveTransaction || unit.IsRollbackOnly
                || unit.Mode != TransactionMode.ReadWrite
                || !ReferenceEquals(unit.Transaction, _transaction)
                || _announcement.Generation <= 0
                || unit.GetTenantContextAnnouncement(_transaction) != _announcement
                || !context.IsResolved || context.TenantId != _announcement.TenantId
                || context.OrganizationId != _announcement.OrganizationId)
            {
                throw new InvalidOperationException("The prepared delivery owner no longer matches its live transaction and context.");
            }
        }

        internal IUnitOfWorkScope Consume(IUnitOfWork unit, ITenantContext context)
        {
            RequireLive(unit, context);
            if (_consumed)
            {
                throw new InvalidOperationException("A prepared delivery owner can be consumed only once.");
            }

            _consumed = true;
            return _frame;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            // Consumption transfers cleanup to the common owner body. Before that,
            // the original frame handle protects against later generations/units.
            if (!_consumed) await _frame.DisposeAsync();
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogRollbackFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(LogRollbackFailure)),
            "Rolling back the ambient transaction for {RequestName} failed. The original exception is rethrown; this one is recorded here only.");
}
