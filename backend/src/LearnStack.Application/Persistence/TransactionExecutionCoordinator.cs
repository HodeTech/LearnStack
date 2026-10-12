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
    ILogger<TransactionExecutionCoordinator> logger)
{
    /// <summary>
    /// Announces before dispatch, retains producer ancestry through completion,
    /// and reports the actual durability boundary (ADR-0057).
    /// </summary>
    public async Task<TResponse> ExecuteAsync<TResponse>(
        IProvisionsTenant? provisioning,
        TransactionMode mode,
        string operationName,
        Func<Task<TResponse>> next,
        CancellationToken cancellationToken = default)
        where TResponse : IResultBase
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        await using var scope = await unitOfWork.BeginTransactionAsync(mode, cancellationToken);
        var committing = false;

        try
        {
            // Provisioning changes SQL authority only; the ambient accessor stays
            // unresolved. A resolved caller cannot announce another tenant here.
            if (!tenantContext.IsResolved && provisioning is not null)
            {
                await unitOfWork.SetProvisioningTenantContextAsync(
                    provisioning.ProvisioningTenantId, cancellationToken);
            }
            else
            {
                await unitOfWork.SetTenantContextAsync(tenantContext, cancellationToken);
            }

            // Capture causal identity at the trusted entry. Nested invocations inherit it;
            // asynchronous work keeps SystemActor effective without impersonating a human.
            var causalActor = tenantContext.IsResolved
                ? tenantContext.CausalActorUserId
                    ?? (tenantContext.UserId == UserId.SystemActor ? null : tenantContext.UserId)
                : null;
            using var invocation = producer.BeginInvocation(scope.IsOwner, causalActor);
            var response = await next();
            if (response.IsFailure)
            {
                // Joiners are not savepoints. Successful production cannot escape
                // merely because an ancestor absorbs this refusal.
                if (invocation.HasProducedWork) unitOfWork.MarkRollbackOnly();
                await scope.FailAsync(CancellationToken.None);
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

                if (invocation.HasProducedWork) _ = producer.RequireAnnouncement();
                await auditStore.WritePendingAsync(unitOfWork, cancellationToken);
            }

            committing = true;
            try
            {
                await scope.CompleteAsync(cancellationToken);
            }
            catch (Exception commitFailure)
            {
                if (scope.IsOwner)
                {
                    if (unitOfWork.IsRollbackOnly) capture.MarkRolledBack();
                    else capture.MarkIndeterminate(commitFailure);
                }

                throw;
            }

            if (scope.IsOwner) capture.MarkCommitted();
            return response;
        }
        catch when (!committing)
        {
            unitOfWork.MarkRollbackOnly();
            // Cleanup must neither be abandoned by cancellation nor replace the
            // original dispatch/audit failure with a bookkeeping exception.
            try
            {
                await scope.FailAsync(CancellationToken.None);
            }
            catch (Exception rollbackFailure)
            {
                LogRollbackFailure(logger, operationName, rollbackFailure);
            }

            if (scope.IsOwner && capture.State != AuditIntentState.Indeterminate)
            {
                capture.MarkRolledBack();
            }

            throw;
        }
    }

    private static readonly Action<ILogger, string, Exception?> LogRollbackFailure =
        LoggerMessage.Define<string>(
            LogLevel.Error,
            new EventId(1, nameof(LogRollbackFailure)),
            "Rolling back the ambient transaction for {RequestName} failed. The original exception is rethrown; this one is recorded here only.");
}
