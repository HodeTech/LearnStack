using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>
/// Mints the trusted-operation descriptor of one admitted operator act, and of nothing else
/// (ADR-0102 § 1).
/// </summary>
/// <remarks>
/// <para>
/// <b>One frame, one act.</b> The executor enters it with the admission and the context it built;
/// <see cref="Capture"/> then answers only for the request types the catalog binds to that
/// admission's operation, in that context object, while the frame is open. Every other request in
/// the scope, and every request outside an entered frame, gets null, which is what the one-descriptor
/// rule of the authorization coordinator needs from a source that does not own the request.
/// </para>
/// <para>
/// The descriptor carries the operator attribution of the admission, which the audit composer
/// records as <c>metadata.operator</c> and the reason slug. It carries no ticket.
/// </para>
/// <para>
/// A frame whose context names an organization is evidence only for a closure's fan-out (ADR-0102
/// Amendment 2), which the executor alone builds that way.
/// </para>
/// </remarks>
internal sealed class PlatformOperatorOperationSource(
    IOperationPolicyCatalog policies,
    PlatformOperatorActCatalog acts,
    ITenantContextAccessor accessor) : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
{
    private Frame? _frame;

    internal IDisposable Enter(PlatformAdmission admission, ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(context);
        if (!CanIssue || _frame is not null || !PlatformOperations.IsRegistered(admission.Operation)
            || !admission.Operation.RunsInPipeline)
        {
            throw new InvalidOperationException("Operator act provenance is invalid.");
        }

        var frame = new Frame(admission, context);
        _frame = frame;
        if (!IsEvidenceCurrent(frame))
        {
            frame.Close();
            throw new InvalidOperationException("Operator act provenance is invalid.");
        }

        return new Scope(frame);
    }

    public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(context);
        if (!CanIssue || _frame is not { } frame || !ReferenceEquals(frame.Context, context) || !IsEvidenceCurrent(frame)
            || !acts.TryGet(requestType, out var act)
            || !ReferenceEquals(act.Operation, frame.Admission.Operation)
            || !policies.TryGet(requestType, out var policy)
            || policy.Kind != OperationPolicyKind.FiniteTrusted
            || policy.AllowedSources is not [AuthorizationSourceKind.PlatformOperator])
        {
            return null;
        }

        return ForOperation(
            AuthorizationSourceKind.PlatformOperator, requestType, context, frame, PlatformOperatorAttribution.From(frame.Admission));
    }

    protected override bool IsEvidenceCurrent(object? evidence) => evidence is Frame frame
        && ReferenceEquals(frame, _frame) && frame.IsOpen
        && frame.Context.HostScope is null && frame.Context.UserId is null
        && (frame.Context.OrganizationId is null
            || ReferenceEquals(frame.Admission.Operation, PlatformOperations.ClosureFanOut))
        && frame.Context.IsResolved && frame.Context.Origin == TenantContextOrigin.Ambient
        && ReferenceEquals(accessor.Current, frame.Context);

    private sealed class Frame(PlatformAdmission admission, ITenantContext context)
    {
        private int _closed;

        internal PlatformAdmission Admission { get; } = admission;

        internal ITenantContext Context { get; } = context;

        internal bool IsOpen => Volatile.Read(ref _closed) == 0;

        internal void Close() => Interlocked.Exchange(ref _closed, 1);

        public override string ToString() => nameof(Frame);
    }

    private sealed class Scope(Frame frame) : IDisposable
    {
        public void Dispose() => frame.Close();
    }
}

/// <summary>Registers the operator-act source and executor over a closed act catalog.</summary>
public static class PlatformOperatorActRegistration
{
    /// <summary>Adds the executor, the catalog and the scoped source (ADR-0102 § 1).</summary>
    public static IServiceCollection AddPlatformOperatorActs(
        this IServiceCollection services, PlatformOperatorActCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(catalog);
        services.AddSingleton(catalog);
        services.TryAddSingleton<PlatformOperatorActExecutor>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<ITrustedOperationSource, PlatformOperatorOperationSource>());
        return services;
    }
}
