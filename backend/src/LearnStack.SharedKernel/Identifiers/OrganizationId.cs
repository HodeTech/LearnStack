using Vogen;

namespace LearnStack.SharedKernel.Identifiers;

/// <summary>
/// A sub-unit within a tenant, per
/// <see href="../../../../docs/decisions/0017-tenant-organization-hierarchy.md">ADR-0017</see>.
/// </summary>
/// <remarks>
/// <para>
/// Cross-cutting for the same reason <see cref="TenantId"/> is: it rides on
/// <c>ITenantContext</c>, on every <c>[OrganizationScoped]</c> entity, in
/// organization-scoped cache keys, in job payloads and on
/// <c>IntegrationEventEnvelope</c>. Identity and every other module hold it **by
/// value** and read organization data through an application contract; the
/// <c>Organization</c> aggregate itself belongs to
/// <c>LearnStack.Modules.Tenancy.Domain</c> and nowhere else
/// (ADR-0017 Amendment 2).
/// </para>
/// <para>
/// <b>Nullable at almost every use site, and the null means something.</b> A
/// tenant-owned row with no organization is <i>tenant-wide</i> — visible to every
/// organization in its tenant — which is why the canonical policy's organization
/// term reads <c>organization_id IS NULL OR organization_id = …</c> rather than an
/// equality alone. It is not "unknown"; it is a scope.
/// </para>
/// <para>
/// Values are minted through the injected <c>IGuidFactory</c>
/// (<c>OrganizationId.From(guidFactory.NewUuidV7())</c>) so a test can pin them,
/// per Standards 02 § Time. Unlike <see cref="TenantId"/> there is no external
/// registry: an organization is created by the tenant that owns it, inside a
/// transaction that has already set <c>app.tenant_id</c>.
/// </para>
/// </remarks>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct OrganizationId : IStronglyTypedId<Guid>;
