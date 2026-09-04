using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;

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
[TenantOwned]
public sealed class TenantLocale : ITenantOwned
{
    private TenantLocale() => Locale = null!;

    public TenantId TenantId { get; private set; }

    /// <summary>
    /// BCP-47 tag in canonical case — <c>tr-TR</c>, <c>en-US</c>, <c>zh-Hans-CN</c>.
    /// </summary>
    /// <remarks>
    /// Canonicalized by <see cref="LocaleTag"/> on the way in, because this is half
    /// of the primary key: <c>en-US</c> and <c>en-us</c> are the same locale and
    /// would otherwise be two rows for one tenant, which is exactly the duplicate
    /// the composite key exists to prevent. The same argument
    /// <c>TenantDomain</c> makes for running its host through
    /// <c>EffectiveHost.Normalize</c>.
    /// </remarks>
    public string Locale { get; private set; }

    /// <summary>Exactly one locale per tenant carries this.</summary>
    public bool IsDefault { get; private set; }

    /// <summary>A disabled locale keeps its translations but is not offered.</summary>
    public bool IsEnabled { get; private set; }

    /// <summary>Display order in a language switcher.</summary>
    public short Sort { get; private set; }

    /// <summary>Makes this the tenant's default locale.</summary>
    /// <remarks>
    /// <c>internal</c>, and reached only from <c>Tenant.PromoteDefault</c>, which clears
    /// the incumbent first. Exposed publicly it would be the one call that can put two
    /// rows past the aggregate guard and into the partial unique index, where the failure
    /// is a 23505 nobody wrote a message for.
    /// </remarks>
    internal void MakeDefault() => IsDefault = true;

    /// <summary>Stops this being the default.</summary>
    internal void ClearDefault() => IsDefault = false;

    internal static TenantLocale Create(
        TenantId tenantId, string locale, bool isDefault, bool isEnabled = true, short sort = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(locale);
        MappedLength.EnsureAtMost(locale, 35, nameof(locale));
        LocaleTag.EnsureWellFormed(locale, nameof(locale));

        TenantOwnership.EnsureRealTenant(tenantId, "A locale belongs to a tenant.", nameof(tenantId));

        return new TenantLocale
        {
            TenantId = tenantId,
            Locale = LocaleTag.Canonicalize(locale),
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
[TenantOwned]
public sealed class TenantFeatureFlag : ITenantOwned
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

    internal static TenantFeatureFlag Create(
        TenantId tenantId, string key, string value, DateTimeOffset at, UserId by)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        MappedLength.EnsureAtMost(key, 200, nameof(key));
        JsonValue.EnsureWellFormed(value, nameof(value));

        // The one type in this module that carries audit columns without
        // deriving from AuditableEntity — a composite natural key cannot — and so
        // the one that skipped its guard. Without it a sentinel timestamp and an
        // uninitialized actor both persist, and the actor surfaces as
        // ValueObjectValidationException out of the Vogen EF converter.
        AuditInput.EnsureValid(at, by);

        TenantOwnership.EnsureRealTenant(
            tenantId, "A feature flag belongs to a tenant.", nameof(tenantId));

        return new TenantFeatureFlag
        {
            TenantId = tenantId,
            Key = key,
            Value = value,
            UpdatedAt = at,
            UpdatedBy = by,
        };
    }

    internal void SetValue(string value, DateTimeOffset at, UserId by)
    {
        JsonValue.EnsureWellFormed(value, nameof(value));
        AuditInput.EnsureValid(at, by);

        Value = value;
        UpdatedAt = at;
        UpdatedBy = by;
    }
}
