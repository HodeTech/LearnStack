using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// A closure Finalize's redaction summed over its passes: one report per participant tenant and one for the platform
/// sentinel tenant, as closed counts and never a person identifier
/// (<see href="../../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 §§ 3, 8</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>Where each part goes.</b> <see cref="ToClosedJson"/> is the closed document the Finalize's platform-wide rows
/// carry in <c>after_state</c> under the platform sentinel tenant: its intent row, which is the redaction's MUST evidence,
/// and its result row. Each participant tenant's own report in <see cref="Tenants"/> is what that tenant's fan-out
/// request records as its retained audit kinds, its tenant's own evidence (ADR-0107 § 8), and the retained kinds of
/// <see cref="Total"/> are what the closure request records.
/// </para>
/// <para>
/// <b>Bounded however many tenants.</b> The document lists the tenant identifiers (not personal data) with the rows the
/// redaction selected in each only up to <see cref="MaximumListedTenants"/>; beyond, it carries the tenant count and the
/// totals alone (ADR-0108 § 8). A listed entry is one identifier and one count, and the platform and total reports are
/// closed objects over the two closed vocabularies, so the document stays under <see cref="MaximumClosedJsonBytes"/>
/// whatever the counts: half of the 32 KiB a row's state may hold, which leaves the rest to the owner's own closed fields.
/// </para>
/// <para>
/// <b>Summing.</b> <see cref="Total"/> adds every pass, the platform pass included: the rows selected, the rows changed
/// per column, and each retained kind any pass reports. A closure's passes all reach their whole tenant and each reports
/// the same three kinds (the audit actor, entity and metadata references), so each total is exact; a kind that some pass
/// left out would be a lower bound, because a report's list is partial and an absent kind is not zero.
/// </para>
/// </remarks>
public sealed record AuditRedactionAggregate
{
    /// <summary>The most tenants whose rows selected the document lists by identifier (ADR-0108 § 8).</summary>
    public const int MaximumListedTenants = 200;

    /// <summary>
    /// The most bytes <see cref="ToClosedJson"/> returns. At the listing's bound, each listed entry is a 36-character
    /// identifier and a count of at most 19 digits (59 bytes with its quotes and separators, 11,800 for 200), and each of
    /// the two reports at most about 1,000 bytes with every column and all sixteen retained kinds at 19 digits.
    /// </summary>
    public const int MaximumClosedJsonBytes = 16 * 1024;

    /// <summary>Sums a Finalize's passes.</summary>
    /// <param name="tenants">Each participant tenant's pass, keyed by its tenant; may be empty when no group is served.</param>
    /// <param name="platform">The pass over the platform sentinel tenant.</param>
    /// <exception cref="ArgumentException">A tenant is unassigned, is the platform sentinel, or has no report.</exception>
    /// <exception cref="OverflowException">A total does not fit a 64-bit count.</exception>
    public AuditRedactionAggregate(IReadOnlyDictionary<TenantId, AuditRedactionReport> tenants, AuditRedactionReport platform)
    {
        ArgumentNullException.ThrowIfNull(tenants);
        ArgumentNullException.ThrowIfNull(platform);
        if (tenants.Any(entry => !StronglyTypedId.IsAssigned(entry.Key) || entry.Key == TenantId.PlatformSentinel || entry.Value is null))
        {
            throw new ArgumentException(
                "A closure's tenant passes are keyed by real participant tenants, never the platform sentinel, and each has its report.",
                nameof(tenants));
        }

        Tenants = tenants.ToFrozenDictionary();
        Platform = platform;
        Total = Sum([.. tenants.Values, platform]);
    }

    /// <summary>Each participant tenant's report.</summary>
    public FrozenDictionary<TenantId, AuditRedactionReport> Tenants { get; }

    /// <summary>The report of the pass over the platform sentinel tenant.</summary>
    public AuditRedactionReport Platform { get; }

    /// <summary>Every pass summed, the platform pass included.</summary>
    public AuditRedactionReport Total { get; }

    /// <summary>Whether <see cref="ToClosedJson"/> lists each tenant's rows selected, which it does up to <see cref="MaximumListedTenants"/>.</summary>
    public bool ListsTenants => Tenants.Count <= MaximumListedTenants;

    /// <summary>
    /// The closed document:
    /// <c>{"tenant_count":n,"rows_selected_by_tenant":{tenant:n,...},"platform":{report},"total":{report}}</c>, where each
    /// report is <see cref="AuditRedactionReport.ToClosedJson"/>'s object and <c>rows_selected_by_tenant</c>, ordered by
    /// tenant identifier, is present only while <see cref="ListsTenants"/>.
    /// </summary>
    public string ToClosedJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("tenant_count", Tenants.Count);
            if (ListsTenants)
            {
                writer.WriteStartObject("rows_selected_by_tenant");
                foreach (var (tenant, report) in Tenants
                    .Select(entry => (Tenant: entry.Key.Value.ToString("D"), entry.Value))
                    .OrderBy(entry => entry.Tenant, StringComparer.Ordinal))
                {
                    writer.WriteNumber(tenant, report.RowsSelected);
                }

                writer.WriteEndObject();
            }

            writer.WritePropertyName("platform");
            Platform.WriteTo(writer);
            writer.WritePropertyName("total");
            Total.WriteTo(writer);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Never prints a tenant.</summary>
    public override string ToString() => nameof(AuditRedactionAggregate);

    private static AuditRedactionReport Sum(IReadOnlyCollection<AuditRedactionReport> reports)
    {
        long selected = 0;
        var redacted = Enum.GetValues<AuditRedactedColumn>().ToDictionary(column => column, _ => 0L);
        var retained = new SortedDictionary<RetainedReferenceKind, long>();
        foreach (var report in reports)
        {
            selected = checked(selected + report.RowsSelected);
            foreach (var (column, count) in report.Redacted)
            {
                redacted[column] = checked(redacted[column] + count);
            }

            foreach (var entry in report.Retained)
            {
                retained[entry.Kind] = checked(retained.GetValueOrDefault(entry.Kind) + entry.Count);
            }
        }

        return new AuditRedactionReport(selected, redacted, [.. retained.Select(entry => new RetainedReferenceCount(entry.Key, entry.Value))]);
    }
}
