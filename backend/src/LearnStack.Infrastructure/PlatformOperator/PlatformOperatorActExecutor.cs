using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>
/// Runs one admitted operator act through the ordinary request pipeline, on the application role,
/// against the tenant the operator named (ADR-0102 § 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The one place an operator's tenant reaches the pipeline.</b> It consumes the admission, opens a
/// fresh scope, installs an immutable resolved <see cref="OperatorTenantContext"/> in the accessor for
/// the length of the act, including for the tenant the act is about to create, enters the one-frame
/// operator source, sends the request, counts how the act ended and restores the accessor. The ordinary
/// transaction behavior announces the installed context, so this adds no tenant setter. The target is
/// never read from a header, a host or an ambient value, and a request outside the closed catalog, or one
/// the admission's operation does not cover, is refused before any scope exists.
/// </para>
/// <para>
/// <b>It is a context writer, and says so.</b> Installing a context in the accessor is what makes the
/// EF query filters and the handlers read the target tenant; the architecture rule that enumerates
/// accessor writers lists this file beside the resolver, the two asynchronous executors and the job
/// runtime. The platform role is not used, and nothing here opens a connection.
/// </para>
/// </remarks>
public sealed partial class PlatformOperatorActExecutor(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor accessor,
    PlatformOperatorActCatalog acts,
    IClock clock,
    ILogger<PlatformOperatorActExecutor> logger)
{
    /// <summary>Runs <paramref name="request"/> for <paramref name="tenantId"/> under <paramref name="admission"/>.</summary>
    /// <exception cref="InvalidOperationException">The admission or the request is not admissible.</exception>
    public async Task<TResponse> ExecuteAsync<TResponse>(
        PlatformAdmission admission,
        TenantId tenantId,
        IRequest<TResponse> request,
        CancellationToken cancellationToken = default)
        where TResponse : IResultBase
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(request);
        if (!PlatformOperations.IsRegistered(admission.Operation) || !admission.Operation.RunsInPipeline
            || !acts.TryGet(request.GetType(), out var act) || !ReferenceEquals(act.Operation, admission.Operation)
            || !StronglyTypedId.IsAssigned(tenantId) || tenantId == TenantId.PlatformSentinel)
        {
            throw new InvalidOperationException("The operator act is not admissible.");
        }

        if ((clock.UtcNow - admission.IssuedAt).Duration() > PlatformAdmission.MaximumAge)
        {
            throw new InvalidOperationException("The platform admission is stale. An act asks the gate again.");
        }

        if (!admission.TryConsume())
        {
            throw new InvalidOperationException("The platform admission was already used. One admission admits one act.");
        }

        var previous = accessor.Current;
        await using var scope = scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        try
        {
            // Install in this async method, not a helper: an AsyncLocal write in a helper does not
            // flow back to this scope's later resolutions.
            ITenantContext context = new OperatorTenantContext(tenantId, admission.Operator.CorrelationId);
            accessor.Current = context;

            var source = services.GetServices<ITrustedOperationSource>().OfType<PlatformOperatorOperationSource>().Single();
            using var frame = source.Enter(admission, context);
            TResponse response;
            try
            {
                response = await services.GetRequiredService<ISender>().Send(request, cancellationToken);
            }
            catch
            {
                PlatformOperatorMetrics.Act(admission.Operation, PlatformOperatorActOutcome.Failed);
                throw;
            }

            if (response.IsSuccess)
            {
                PlatformOperatorMetrics.Act(
                    admission.Operation,
                    services.GetRequiredService<OperatorActGuard>().Replayed
                        ? PlatformOperatorActOutcome.Replayed : PlatformOperatorActOutcome.Applied);
            }
            else
            {
                PlatformOperatorMetrics.Act(admission.Operation, PlatformOperatorActOutcome.Refused);
                if (response.Error?.Code == "forbidden")
                {
                    // An admitted act the pipeline itself refused means the request, source or context was not what
                    // this executor built: a defect, not an operator's mistake, and it writes no audit row.
                    LogPipelineRefused(logger, admission.Operation.AuditSlug);
                }
            }

            return response;
        }
        finally
        {
            accessor.Current = previous;
        }
    }

    [LoggerMessage(
        EventId = 7109,
        Level = LogLevel.Warning,
        Message = "An admitted operator act {Operation} was refused by the request pipeline as forbidden; the request, source or context was not what the executor built.")]
    private static partial void LogPipelineRefused(ILogger logger, string operation);
}
