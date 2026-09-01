using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Tenancy.Domain;

/// <summary>
/// One piece of non-translated tenant configuration, optionally overridden for a
/// single organization.
/// </summary>
/// <remarks>
/// <para>
/// <b>Key/value with a <c>jsonb</c> payload, not a table of typed columns.</b>
/// The corpus fixes the contents and never the shape, and
/// <see href="../../../../../docs/standards/05-database.md">Database Standards
/// § Constraints</see> supplies the decision procedure: an organization setting is
/// an override resolved through the documented org → tenant fallback chain, which
/// is the *authored precedence* branch, so <see cref="OrganizationId"/> belongs
/// in the key and the constraint is
/// <c>UNIQUE NULLS NOT DISTINCT (tenant_id, organization_id, key)</c>. Without
/// <c>NULLS NOT DISTINCT</c> a tenant could hold unlimited duplicate tenant-wide
/// rows for one key — the rows a single-organization tenant creates exclusively —
/// and resolution would pick one arbitrarily.
/// </para>
/// <para>
/// <b>The only organization-scoped table in the Packet 6 set</b>, and therefore
/// the only one that takes the two <c>AS RESTRICTIVE</c> write guards and the
/// <c>organization_id</c> immutability trigger. A null organization means
/// <i>tenant-wide</i> — a scope, not "unknown".
/// </para>
/// </remarks>
[TenantOwned]
[OrganizationScoped]
public sealed class TenantSetting : AuditableEntity<TenantSettingId>, IOrganizationScoped
{
    private TenantSetting(TenantSettingId id)
        : base(id)
    {
        Key = null!;
        Value = null!;
    }

    // EF materialization.
    private TenantSetting()
    {
        Key = null!;
        Value = null!;
    }

    public TenantId TenantId { get; private set; }

    /// <summary>Null means the setting applies tenant-wide.</summary>
    public OrganizationId? OrganizationId { get; private set; }

    /// <summary>Dotted key, e.g. <c>notifications.default-sender</c>.</summary>
    public string Key { get; private set; }

    /// <summary>The value, as JSON. The shape is the caller's to know.</summary>
    public string Value { get; private set; }

    public static TenantSetting Create(
        TenantSettingId id,
        TenantId tenantId,
        OrganizationId? organizationId,
        string key,
        string value,
        IClock clock,
        UserId createdBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        MappedLength.EnsureAtMost(key, 200, nameof(key));
        JsonValue.EnsureWellFormed(value, nameof(value));

        if (!id.IsInitialized() || id.Value == Guid.Empty)
        {
            throw new ArgumentException(
                "The identifier was never assigned; construct it through its factory.",
                nameof(id));
        }

        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException("A setting belongs to a tenant.", nameof(tenantId));
        }

        // The nullable says "tenant-wide or organization-scoped". It does not say
        // "an uninitialized id is fine": an unset Vogen wrapper inside the
        // nullable persisted as far as the EF converter and surfaced there as
        // ValueObjectValidationException, three layers from this call.
        if (organizationId is { } organization
            && (!organization.IsInitialized() || organization.Value == Guid.Empty))
        {
            throw new ArgumentException(
                "An organization-scoped setting names a real organization; pass null for a "
                + "tenant-wide one.",
                nameof(organizationId));
        }

        var setting = new TenantSetting(id)
        {
            TenantId = tenantId,
            OrganizationId = organizationId,
            Key = key,
            Value = value,
        };

        setting.MarkCreated(clock.UtcNow, createdBy);
        return setting;
    }

    /// <summary>
    /// Replaces the value. The scope is not changeable.
    /// </summary>
    /// <remarks>
    /// There is deliberately no method to move a setting between organizations.
    /// The database refuses it too — <c>tg_tenant_settings_organization_id_immutable</c>
    /// fires on any <c>UPDATE</c> that changes the column, including to or from
    /// null — because a row's audit trail, storage prefix and cache-key prefix are
    /// all organization-qualified, so re-parenting would orphan three subsystems
    /// at once. Moving a setting is a create plus a delete.
    /// </remarks>
    public void SetValue(string value, IClock clock, UserId updatedBy)
    {
        ArgumentNullException.ThrowIfNull(clock);
        JsonValue.EnsureWellFormed(value, nameof(value));

        // Stamped first — see Tenant.ChangeStatus.
        MarkUpdated(clock.UtcNow, updatedBy);
        Value = value;
    }
}
