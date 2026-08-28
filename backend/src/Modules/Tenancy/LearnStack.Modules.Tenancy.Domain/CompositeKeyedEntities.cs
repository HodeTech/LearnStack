using LearnStack.SharedKernel.Domain;
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
        MappedLength.EnsureAtMost(locale, 35, nameof(locale));
        LocaleTag.EnsureWellFormed(locale, nameof(locale));

        TenantOwned.EnsureRealTenant(tenantId, "A locale belongs to a tenant.", nameof(tenantId));

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
        MappedLength.EnsureAtMost(key, 200, nameof(key));
        JsonValue.EnsureWellFormed(value, nameof(value));

        // The one type in this module that carries audit columns without
        // deriving from AuditableEntity — a composite natural key cannot — and so
        // the one that skipped its guard. Without it a sentinel timestamp and an
        // uninitialized actor both persist, and the actor surfaces as
        // ValueObjectValidationException out of the Vogen EF converter.
        AuditInput.EnsureValid(at, by);

        TenantOwned.EnsureRealTenant(
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

    public void SetValue(string value, DateTimeOffset at, UserId by)
    {
        JsonValue.EnsureWellFormed(value, nameof(value));
        AuditInput.EnsureValid(at, by);

        Value = value;
        UpdatedAt = at;
        UpdatedBy = by;
    }
}

/// <summary>
/// Guards a value against the length its column holds.
/// </summary>
/// <remarks>
/// The database rejects a longer value with <c>22001</c>, which names neither the
/// property nor the aggregate and arrives three layers from the call that produced
/// it. The numbers here are the ones the EF configurations map; asserting them at
/// the factory is what makes the failure say which field is wrong.
/// </remarks>
internal static class MappedLength
{
    public static void EnsureAtMost(string value, int maximum, string parameterName)
    {
        if (value.Length > maximum)
        {
            throw new ArgumentException(
                $"The value is {value.Length} characters; the column holds {maximum}.",
                parameterName);
        }
    }
}

/// <summary>
/// Guards a value on its way into a <c>jsonb</c> column.
/// </summary>
/// <remarks>
/// PostgreSQL rejects malformed JSON on the insert with <c>22P02</c>, three
/// layers from the call that produced it and naming neither the property nor the
/// aggregate. Parsing here is one pass over a value the caller already holds, and
/// it turns that into an <c>ArgumentException</c> at the call site — the same
/// reason <c>TenantDomain</c> runs the host through <c>EffectiveHost.Normalize</c>
/// rather than waiting for its CHECK.
/// </remarks>
internal static class JsonValue
{
    public static void EnsureWellFormed(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);

        try
        {
            using var _ = System.Text.Json.JsonDocument.Parse(value);
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new ArgumentException(
                $"The value is not well-formed JSON and the column is jsonb: {exception.Message}",
                parameterName,
                exception);
        }
    }
}

/// <summary>
/// Guards a tenant-owned row's owning identifier.
/// </summary>
/// <remarks>
/// <c>IsInitialized()</c> alone is not enough: <c>TenantId.From(Guid.Empty)</c>
/// reports initialized, and a nil-uuid tenant then inserts and satisfies its own
/// policy whenever <c>app.tenant_id</c> holds the same nil. No ADR reserves the
/// nil uuid — the platform sentinel is deliberately unfixed until Packet 9 — so
/// this is refused at the factory rather than left to collide with whatever that
/// packet chooses.
/// </remarks>
internal static class TenantOwned
{
    public static void EnsureRealTenant(TenantId tenantId, string message, string parameterName)
    {
        if (!tenantId.IsInitialized() || tenantId.Value == Guid.Empty)
        {
            throw new ArgumentException(message, parameterName);
        }
    }
}

/// <summary>
/// Guards a slug against the shape its column's consumers assume.
/// </summary>
/// <remarks>
/// A tenant slug appears in hostnames and an organization slug is documented as a
/// DNS label, so both are lowercase alphanumeric with single interior hyphens.
/// Neither factory looked at the characters, and neither column has a CHECK —
/// <c>platform_host_to_tenant</c> and <c>tenant_domains</c> carry the host
/// normalization constraint, the slug tables do not — so a slug with a slash or
/// an uppercase letter reached a hostname unchallenged.
/// </remarks>
internal static partial class UrlSlug
{
    public static void EnsureUrlSafe(string value, string parameterName)
    {
        if (!Pattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a URL-safe slug: lowercase letters, digits and single "
                + "interior hyphens only.",
                parameterName);
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}

/// <summary>
/// Guards a locale tag against BCP-47 well-formedness.
/// </summary>
/// <remarks>
/// <see href="../../../../../docs/architecture/12-localization.md">12-localization.md</see>
/// says the column bounds the length and "well-formedness itself is validated in
/// application code, not by this column". This is that code: without it a
/// 35-character run of one letter was a locale, and the repository's own test
/// pinned that as correct.
/// </remarks>
internal static partial class LocaleTag
{
    public static void EnsureWellFormed(string value, string parameterName)
    {
        // Shape first, then the framework. CultureInfo alone is not a check:
        // .NET treats an unknown but well-formed tag as a valid custom culture,
        // and on some platforms accepts tags this pattern rejects.
        if (!Pattern().IsMatch(value))
        {
            throw new ArgumentException(
                $"'{value}' is not a well-formed BCP-47 language tag — expected forms are "
                + "'tr', 'tr-TR', 'zh-Hans', 'zh-Hans-CN'.",
                parameterName);
        }
    }

    // language[-script][-region][-variant…]: 2-3 letter (or 4-8 for registered
    // subtags) primary, optional 4-letter script, optional 2-letter or 3-digit
    // region, then variant subtags.
    [System.Text.RegularExpressions.GeneratedRegex(
        "^[a-zA-Z]{2,8}(-[a-zA-Z]{4})?(-([a-zA-Z]{2}|[0-9]{3}))?(-([a-zA-Z0-9]{5,8}|[0-9][a-zA-Z0-9]{3}))*$")]
    private static partial System.Text.RegularExpressions.Regex Pattern();
}
