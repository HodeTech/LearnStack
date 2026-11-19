using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>
/// Runs one admitted operator act through the ordinary request pipeline, on the application role,
/// against the tenant the operator named (ADR-0102 § 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>The confined tenant setter.</b> This is the one place a tenant chosen by an operator reaches the
/// pipeline. It consumes the admission, opens a fresh scope, installs an immutable
/// <see cref="OperatorTenantContext"/> in the accessor for the length of the act (or leaves the
/// context unresolved when the act creates its tenant, which then announces itself through
/// <c>IProvisionsTenant</c> as the seed's provisioning does), enters the one-frame operator source,
/// sends the request and restores the accessor. The target is never read from a header, a host or an
/// ambient value, and a request outside the closed catalog, or one the admission's operation does not
/// cover, is refused before any scope exists.
/// </para>
/// <para>
/// <b>It is a context writer, and says so.</b> Installing a context in the accessor is what makes the
/// EF query filters and the handlers read the target tenant; the architecture rule that enumerates
/// accessor writers lists this file beside the resolver, the two asynchronous executors and the job
/// runtime. The platform role is not used, and nothing here opens a connection.
/// </para>
/// </remarks>
public sealed class PlatformOperatorActExecutor(
    IServiceScopeFactory scopeFactory,
    ITenantContextAccessor accessor,
    PlatformOperatorActCatalog acts,
    IClock clock)
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
        if (!PlatformOperations.IsRegistered(admission.Operation) || admission.Operation.NeedsPlatformRole
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
            return await services.GetRequiredService<ISender>().Send(request, cancellationToken);
        }
        finally
        {
            accessor.Current = previous;
        }
    }
}
