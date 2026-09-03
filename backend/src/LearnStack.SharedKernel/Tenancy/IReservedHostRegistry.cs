namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// The hosts a deployment has reserved for itself, which no tenant may map.
/// </summary>
/// <remarks>
/// <para>
/// <b>The check <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// assigns to whichever packet builds the host-mapping writer.</b> A host on
/// <c>Tenancy:PlatformHosts</c> classifies <c>PlatformHost</c> before the resolver is
/// called at all, so a <c>platform_host_to_tenant</c> row naming the same host is inert —
/// "never read, never logged, never counted". The precedence is right: the list is the
/// operator's own entry point and a tenant must not be able to take it over. What is
/// wrong is that the losing row is <b>silent</b>, so the deployment that created one gets
/// no signal until someone wonders why a mapping does nothing.
/// </para>
/// <para>
/// <b>A port because the two live in different places.</b> The list is application
/// configuration bound in <c>LearnStack.Api</c>; the row is a table written from a module.
/// A module may not reference the composition root, and a database constraint cannot see
/// configuration — which is exactly why ADR-0036 says there is no startup cross-check and
/// no constraint, and makes it the writer's job instead.
/// </para>
/// </remarks>
public interface IReservedHostRegistry
{
    /// <summary>
    /// <c>true</c> when <paramref name="normalizedHost"/> is a deployment host.
    /// </summary>
    /// <remarks>
    /// The argument is compared ordinally against entries
    /// <see cref="EffectiveHost.Normalize"/> produced, so a caller passes a normalized
    /// host or gets a false negative — which is the answer that lets the silent row
    /// through.
    /// </remarks>
    bool IsReserved(string normalizedHost);
}

/// <summary>
/// The registry for a deployment that reserves nothing.
/// </summary>
/// <remarks>
/// Not a convenience: a host with no configured platform hosts is an ordinary deployment
/// — every host is then a tenant host or an unknown one — and the seeder and tests need
/// an answer without binding configuration they do not have.
/// </remarks>
public sealed class NoReservedHosts : IReservedHostRegistry
{
    public static NoReservedHosts Instance { get; } = new();

    public bool IsReserved(string normalizedHost) => false;
}
