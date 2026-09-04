namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// An <see cref="ITenantContextAccessor"/> holding one context for its lifetime,
/// for the hosts that have no ambient one to read.
/// </summary>
/// <remarks>
/// Two callers, and neither is a request path: the design-time
/// <c>IDesignTimeDbContextFactory</c>, where <c>dotnet ef</c> builds a model and
/// there is no tenant to resolve, and a test building a context for its model
/// alone. The production accessor is <c>AsyncLocal</c>-backed and registered as a
/// singleton at the composition root; this one is not a substitute for it.
/// </remarks>
public sealed class StaticTenantContextAccessor(ITenantContext? current) : ITenantContextAccessor
{
    /// <summary>An accessor holding nothing, which reads as an unresolved tenant.</summary>
    public static StaticTenantContextAccessor Unresolved { get; } = new(null);

    public ITenantContext? Current { get; set; } = current;
}
