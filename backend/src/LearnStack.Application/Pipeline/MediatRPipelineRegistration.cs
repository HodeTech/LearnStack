using FluentValidation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace LearnStack.Application.Pipeline;

/// <summary>
/// Composition-root extension that registers the canonical eight-step MediatR
/// pipeline (ADR-0032 § Sub-decision 2). Outermost (validation) first,
/// innermost (handler) last; the architecture test
/// <c>MediatR_Pipeline_Order_Matches_Canonical_Sequence</c> asserts the DI
/// registration order at startup.
/// </summary>
public static class MediatRPipelineRegistration
{
    /// <summary>
    /// The 7 pipeline behaviors in canonical order. ADR-0032
    /// § Sub-decision 2 describes the chain as "eight steps" — these 7
    /// behaviors plus the handler at the innermost position make up that
    /// sequence. MediatR resolves the handler after the behavior chain
    /// unwinds; it is not registered here. Architecture tests reflect on
    /// this list to assert the runtime DI order matches the contract;
    /// **do not** reorder without amending ADR-0032.
    /// </summary>
    public static IReadOnlyList<Type> CanonicalBehaviorOrder { get; } =
    [
        typeof(ValidationBehavior<,>),
        typeof(LoggingBehavior<,>),
        typeof(AuditLogBehavior<,>),
        typeof(TenantContextBehavior<,>),
        typeof(AuthorizationBehavior<,>),
        typeof(TransactionBehavior<,>),
        typeof(OutboxFlushBehavior<,>),
        // Step 8 (the handler) is resolved by MediatR itself.
    ];

    /// <summary>
    /// Registers the eight-step MediatR pipeline against the supplied
    /// <paramref name="services"/>. The handler types themselves are scanned
    /// from <paramref name="handlerAssemblies"/> (typically each module's
    /// <c>AssemblyMarker</c> assembly).
    /// </summary>
    public static IServiceCollection AddLearnStackMediatRPipeline(
        this IServiceCollection services,
        params System.Reflection.Assembly[] handlerAssemblies)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(handlerAssemblies);

        // MediatR 12.x throws if no assemblies are registered for handler
        // scanning. Fall back to the LearnStack.Application assembly itself —
        // it carries no handlers in Phase 02a, but the behaviors below register
        // through AddBehavior directly and do not depend on scanning.
        var assembliesToScan = handlerAssemblies.Length > 0
            ? handlerAssemblies
            : [typeof(AssemblyMarker).Assembly];

        services.AddMediatR(cfg =>
        {
            cfg.RegisterServicesFromAssemblies(assembliesToScan);

            foreach (var behaviorType in CanonicalBehaviorOrder)
            {
                cfg.AddBehavior(typeof(IPipelineBehavior<,>), behaviorType);
            }
        });

        // The same assemblies, for the same reason, and it was missing. ValidationBehavior
        // resolves IEnumerable<IValidator<TRequest>> from the container, so without this
        // every validator in the solution is a class nothing constructs — the behavior
        // sees an empty array, short-circuits, and a command with a validator is refused
        // by nothing. It shipped that way only because no validator existed yet; the
        // first one would have been silently inert.
        //
        // The kernel assembly is always in the list, not only in the fallback: a shared
        // validator placed beside the behavior that consumes it would otherwise be as
        // inert as the ones this line exists to register, and inert in the one place
        // nobody would think to check.
        services.AddValidatorsFromAssemblies(
            assembliesToScan.Append(typeof(AssemblyMarker).Assembly).Distinct(),
            includeInternalTypes: true);

        return services;
    }

}
