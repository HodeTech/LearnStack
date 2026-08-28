using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Tenancy.Domain;

/// <summary>
/// A locale a tenant publishes in, per
/// <see href="../../../../../docs/decisions/0008-localization-schema.md">ADR-0008</see>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Composite natural key, no surrogate id, no <c>AuditableEntity</c> base.</b>
/// The published shape is <c>PRIMARY KEY (tenant_id, locale)</c>
/// (<see href="../../../../../docs/architecture/12-localization.md">12-localization.md</see>),
/// and a locale row has no identity beyond the pair it is: a second row for the
/// same tenant and locale is not a second locale, it is a duplicate. Adding a
/// surrogate id to satisfy <c>AuditableEntity&lt;TId&gt;</c> would invent an
/// identity the domain does not have and contradict published DDL other documents
/// already reference.
/// </para>
/// <para>
/// It follows that these rows carry no audit columns and no <c>row_version</c>.
/// That is deliberate: they are a small, wholly-replaced set that the tenant's
/// own configuration audit covers as one change, not six.
/// </para>
/// </remarks>
public sealed class TenantLocale
{
    private TenantLocale() => Locale = null!;

    public TenantId TenantId { get; private set; }

    /// <summary>BCP-47 tag, lowercase region included — <c>tr-TR</c>, <c>en-US</c>.</summary>
    public string Locale { get; private set; }

    /// <summary>Exactly one locale per tenant carries this.</summary>
    public bool IsDefault { get; private set; }

    /// <summary>A disabled locale keeps its translations but is not offered.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Display order in a language switcher.</summary>
    public short Sort { get; private set; }

    public static TenantLocale Create(
        TenantId tenantId, string locale, bool isDefault, bool isEnabled = true, short sort = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);

        if (!tenantId.IsInitialized())
        {
            throw new ArgumentException("A locale belongs to a tenant.", nameof(tenantId));
        }

        return new TenantLocale
        {
            TenantId = tenantId,
            Locale = locale,
            IsDefault = isDefault,
            IsEnabled = isEnabled,
            Sort = sort,
        };
    }
}

/// <summary>
/// A tenant-level feature-flag override — experimental, rollout, opt-in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not plan-level features.</b> Those live in the entitlement projection
/// (<see href="../../../../../docs/decisions/0021-feature-based-entitlement.md">ADR-0021</see>)
/// and are written only by <c>IEntitlementProvider.RefreshAsync</c>. This table is
/// the tenant's own switches, which is why a tenant may write it and may not write
/// the projection.
/// </para>
/// <para>
/// Composite natural key <c>(tenant_id, key)</c> and no surrogate id, for the same
/// reason as <see cref="TenantLocale"/>. It carries <c>updated_at</c> /
/// <c>updated_by</c> because a flag flip is worth attributing, but not the full
/// <c>AuditableEntity</c> set: a flag has no creation event distinct from its
/// first write, and no soft delete — removing a flag removes the row.
/// </para>
/// </remarks>
public sealed class TenantFeatureFlag
{
    private TenantFeatureFlag()
    {
        Key = null!;
        Value = null!;
    }

    public TenantId TenantId { get; private set; }

    public string Key { get; private set; }

    /// <summary>The flag's value as JSON — a boolean, a rollout percentage, a variant name.</summary>
    public string Value { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public UserId UpdatedBy { get; private set; }

    public static TenantFeatureFlag Create(
        TenantId tenantId, string key, string value, DateTimeOffset at, UserId by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        if (!tenantId.IsInitialized())
        {
            throw new ArgumentException("A feature flag belongs to a tenant.", nameof(tenantId));
        }

        return new TenantFeatureFlag
        {
            TenantId = tenantId,
            Key = key,
            Value = value,
            UpdatedAt = at,
            UpdatedBy = by,
        };
    }

    public void SetValue(string value, DateTimeOffset at, UserId by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        Value = value;
        UpdatedAt = at;
        UpdatedBy = by;
    }
}
