using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Tenancy;

/// <summary>
/// A resolved tenant context. Constructed only by <see cref="TenantContextFactory"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The constructor is <c>internal</c>, and that is the ceiling C# offers here.</b>
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Rules</see> asks for "sealed with no public constructor" and names
/// <see cref="TenantContextFactory"/> as a separate type. C# has no friend types, so
/// a <c>private</c> constructor and a top-level factory are mutually exclusive —
/// the two shipped private-constructor types in this corpus, <c>EventTenantContext</c>
/// and <c>HostClassification</c>, both put their factories <i>on</i> the type. The
/// ADR's wording is satisfied exactly by <c>internal</c>: the assembly carries no
/// <c>InternalsVisibleTo</c>, so every module project, both infrastructure
/// assemblies, the API and all four test assemblies are blocked by the compiler.
/// The residual — a second caller inside this one assembly — is covered by the
/// separate <c>TenantContext_Is_Instantiated_In_One_File</c>, which is a source scan:
/// its sibling is reflection-only, and no type-reference test can see a call site.
/// </para>
/// <para>
/// <b>Every instance is complete.</b> There is no setter, no builder and no partial
/// state: the factory returns <c>Result.Fail</c> on any disagreement rather than a
/// context with some fields filled in. A half-populated tenant context is the
/// failure this shape exists to make unrepresentable — <c>IsResolved</c> is
/// <c>true</c> for the lifetime of the object, and every reader may take
/// <see cref="TenantId"/> without a gate.
/// </para>
/// </remarks>
public sealed class TenantContext : ITenantContext
{
    internal TenantContext(
        TenantId tenantId,
        OrganizationId? organizationId,
        UserId? userId,
        TenantContextOrigin origin,
        string? correlationId)
    {
        TenantId = tenantId;
        OrganizationId = organizationId;
        UserId = userId;
        Origin = origin;
        CorrelationId = correlationId;
    }

    /// <inheritdoc />
    public bool IsResolved => true;

    /// <inheritdoc />
    public TenantId TenantId { get; }

    /// <inheritdoc />
    public OrganizationId? OrganizationId { get; }

    /// <inheritdoc />
    public UserId? UserId { get; }

    /// <inheritdoc />
    public TenantContextOrigin? Origin { get; }

    /// <inheritdoc />
    public string? CorrelationId { get; }

    /// <summary>
    /// Always <c>null</c> here. Not for want of routing — it has already run by the
    /// time the resolver executes — but because no endpoint metadata names an owning
    /// module, so an HTTP-resolved context has nothing truthful to put here. The
    /// consumers that do know theirs set it: <c>EventTenantContext</c> takes it from
    /// the subscription.
    /// </summary>
    public string? ModuleName => null;
}
