using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace LearnStack.Infrastructure.Audit.Capture;

/// <summary>
/// Takes the before / after snapshot of every entity a <c>SaveChanges</c> is about to
/// write, and puts it in the request's <see cref="IAuditStateCapture"/>.
/// </summary>
/// <remarks>
/// <para>
/// <b>It captures only.</b> It constructs no row, issues no SQL, and returns the
/// unmodified <see cref="InterceptionResult{TResult}"/>. Several flushes may occur
/// inside one transaction — <c>ProvisionTenantCommand</c> saves three times — which is
/// exactly why the row is composed later, once, by the frame that owns the commit.
/// </para>
/// <para>
/// <b>Every entity in the tracker, minus a named exclusion list.</b> The
/// <c>AuditableEntity&lt;&gt;</c> predicate an earlier draft used is withdrawn: five
/// shipped entities the two module matrices classify MUST carry no such base class, and
/// <c>PlatformHostMapping</c> — the row that decides whose data an anonymous request
/// sees — is the one both matrices single out as mattering most
/// (<see href="../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 7</see>).
/// </para>
/// <para>
/// <b>It must be attached through <c>AddInterceptors</c> on the options.</b> Registering
/// an <see cref="ISaveChangesInterceptor"/> in DI alone does not attach it under this
/// repository's hand-built options shape — measured on EF Core 10, and the reason
/// <c>ModuleDbContextRegistration</c> resolves the interceptors from the provider and
/// passes them in explicitly.
/// </para>
/// <para>
/// <b>Two gates run inside the capture, before anything reaches the buffer</b>
/// (<see href="../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 8</see>):
/// the <see cref="PiiSensitiveAttribute"/> marker together with
/// <see cref="SensitiveTokenCatalog"/>'s name tokens, and the size cap. A sensitive
/// value is <em>replaced</em> rather than dropped, so the diff still records that it
/// changed — which is the fact a reviewer is usually asking about.
/// </para>
/// </remarks>
public sealed class AuditChangeTrackerInterceptor(IAuditStateCapture capture)
    : SaveChangesInterceptor
{
    /// <summary>
    /// Entity type names this interceptor never captures.
    /// </summary>
    /// <remarks>
    /// By name and with a reason each. <c>OutboxMessage</c>: its payload is the audited
    /// event and the row is machinery. <c>IdempotencyKey</c>: request plumbing.
    /// <c>AuditEntry</c> / <c>AuditConfig</c>: the audit tables themselves — which cannot
    /// arise anyway, because the store writes parameterised SQL and never a
    /// <c>DbContext</c>, so there is no re-entrancy path. They are listed regardless,
    /// because "cannot arise" is a property of today's store and the exclusion is a
    /// property of the design.
    /// </remarks>
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        "OutboxMessage",
        "IdempotencyKey",
        "AuditEntry",
        "AuditConfig",
    };

    /// <summary>
    /// Whether a property is sensitive, keyed by the declaring type and property name.
    /// </summary>
    /// <remarks>
    /// Reflection per property per save would be paid on every audited write. The answer
    /// cannot change for the life of the process — an attribute is compile-time and the
    /// token list is a static — so it is computed once.
    /// </remarks>
    private static readonly ConcurrentDictionary<(Type, string), bool> SensitivityCache = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Capture(eventData.Context);

        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Capture(eventData.Context);

        return ValueTask.FromResult(result);
    }

    /// <summary>
    /// Snapshots every captured entity this context is about to write.
    /// </summary>
    /// <remarks>
    /// Public because it is what this class does, not because a test needs it — though a
    /// test is the second caller. EF reaches it through the two <c>SavingChanges</c>
    /// hooks above; anything else that knows a <see cref="DbContext"/> is about to flush
    /// can reach it directly, and driving it that way needs no connection because a
    /// <c>ChangeTracker</c> does not have one. A <c>null</c> context is a no-op rather
    /// than a throw: EF passes one for a few diagnostic events, and an interceptor that
    /// threw there would fail a save for a reason unrelated to the save.
    /// </remarks>
    public void Capture(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        // BEFORE the save, which is the only moment both states exist: after it, EF has
        // accepted the changes and OriginalValues equals CurrentValues.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (!IsCaptured(entry))
            {
                continue;
            }

            capture.Add(Describe(entry));
        }
    }

    private static bool IsCaptured(EntityEntry entry) =>
        entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted
        && !Excluded.Contains(entry.Metadata.ClrType.Name);

    private static CapturedEntityChange Describe(EntityEntry entry)
    {
        var type = entry.Metadata.ClrType;
        var fields = new List<CapturedFieldChange>();

        // An entity-qualified RFC 6901 pointer, because `changes` is one array for the
        // whole request and a bare `/slug` would not say whose slug it was.
        var prefix = "/" + type.Name + "/";

        var before = new StringBuilder("{");
        var after = new StringBuilder("{");
        var first = true;

        foreach (var property in entry.Properties.OrderBy(p => p.Metadata.Name, StringComparer.Ordinal))
        {
            var name = property.Metadata.Name;
            var sensitive = IsSensitive(type, name);

            var beforeJson = entry.State == EntityState.Added
                ? null
                : Value(property.OriginalValue, sensitive);

            var afterJson = entry.State == EntityState.Deleted
                ? null
                : Value(property.CurrentValue, sensitive);

            if (!first)
            {
                before.Append(',');
                after.Append(',');
            }

            first = false;

            var key = AuditJson.Quote(name);
            before.Append(key).Append(':').Append(beforeJson ?? AuditJson.Null);
            after.Append(key).Append(':').Append(afterJson ?? AuditJson.Null);

            // Only what CHANGED, on a modify. On an insert and a delete every property
            // is the change, which is what makes the diff and the snapshot agree.
            if (entry.State != EntityState.Modified || property.IsModified)
            {
                fields.Add(new CapturedFieldChange(prefix + name, beforeJson, afterJson));
            }
        }

        before.Append('}');
        after.Append('}');

        return new CapturedEntityChange(
            EntityType: type.Name,
            EntityId: KeyOf(entry),
            BeforeJson: entry.State == EntityState.Added
                ? null
                : AuditJson.CapObject(before.ToString()),
            AfterJson: entry.State == EntityState.Deleted
                ? null
                : AuditJson.CapObject(after.ToString()),
            Fields: fields);
    }

    private static string Value(object? value, bool sensitive) =>
        sensitive ? AuditJson.Quote(SensitiveTokenCatalog.RedactedValue) : AuditJson.Render(value);

    /// <summary>
    /// The entity's primary key rendered as text, or <c>null</c> for a keyless shape.
    /// </summary>
    /// <remarks>
    /// A composite key is joined with <c>/</c> in key order, which is the same rendering
    /// the pointer above uses for a path segment — so a reader that can read one can read
    /// the other. <c>tenant_level_taxonomy_items</c> is the shipped four-column case.
    /// </remarks>
    private static string? KeyOf(EntityEntry entry)
    {
        var key = entry.Metadata.FindPrimaryKey();

        if (key is null)
        {
            return null;
        }

        var parts = key.Properties
            .Select(property => entry.Property(property.Name).CurrentValue?.ToString() ?? string.Empty)
            .ToList();

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    private static bool IsSensitive(Type declaringType, string propertyName) =>
        SensitivityCache.GetOrAdd(
            (declaringType, propertyName),
            static key =>
            {
                var (type, name) = key;

                // The marker first, because it is the explicit statement and the cheaper
                // lookup. The token list runs beside it rather than instead of it: the
                // list catches what nobody marked, the marker catches what the list's
                // tokens do not name.
                var property = type.GetProperty(
                    name,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                return property?.GetCustomAttribute<PiiSensitiveAttribute>() is not null
                    || SensitiveTokenCatalog.IsSensitive(name);
            });
}
