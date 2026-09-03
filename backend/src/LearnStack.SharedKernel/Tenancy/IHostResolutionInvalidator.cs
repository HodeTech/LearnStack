namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// Closes the window in which a host that just became resolvable still answers 404.
/// </summary>
/// <remarks>
/// <para>
/// <b>The invalidation
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036</see>
/// asks for "on the transaction that flips either flag".</b> A host that resolved to
/// nothing is negative-cached, and before Packet 7 no writer of
/// <c>platform_host_to_tenant</c> existed, so the TTL was the whole of the mechanism —
/// a host activated inside it kept its 404 for the rest of it. That is the exact symptom
/// a developer meets when they load a seeded host once before running the seed.
/// </para>
/// <para>
/// <b>A port because the cache is infrastructure and the writer is a module.</b> Same
/// reason as <see cref="IReservedHostRegistry"/>; the default implementation forgets
/// nothing, which is correct for a host that has no cache in front of it.
/// </para>
/// </remarks>
public interface IHostResolutionInvalidator
{
    /// <summary>Forgets any cached answer for <paramref name="normalizedHost"/>.</summary>
    void Invalidate(string normalizedHost);
}

/// <summary>The invalidator for a host with nothing to invalidate.</summary>
public sealed class NullHostResolutionInvalidator : IHostResolutionInvalidator
{
    public static NullHostResolutionInvalidator Instance { get; } = new();

    public void Invalidate(string normalizedHost)
    {
        // Nothing is cached, so nothing is stale.
    }
}
