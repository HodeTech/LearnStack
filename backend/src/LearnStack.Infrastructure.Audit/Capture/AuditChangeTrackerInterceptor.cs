using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

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
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 7</see>).
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
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 8</see>):
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
    /// Properties left out of the snapshot and the diff.
    /// </summary>
    /// <remarks>
    /// Bookkeeping the audit row already carries or does not want, named by
    /// <see href="../../../../docs/architecture/31-audit-subsystem.md">Audit Subsystem
    /// § 3</see>. <c>TenantId</c> is the row's own <c>tenant_id</c>, so repeating it in
    /// the snapshot says nothing; <c>CreatedAt</c>, <c>UpdatedAt</c> and
    /// <c>RowVersion</c> move on every write, so a diff carrying them buries the property
    /// that actually changed under three that always do.
    /// <para>
    /// The soft-delete pair is deliberately <b>not</b> here. <c>DeletedAt</c> moving is
    /// the whole content of a soft delete, and the actor columns are who did it — both
    /// are the record rather than the bookkeeping around it.
    /// </para>
    /// </remarks>
    private static readonly HashSet<string> NotSnapshotted = new(StringComparer.Ordinal)
    {
        "TenantId",
        "CreatedAt",
        "UpdatedAt",
        "RowVersion",
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

        var snapshotted = entry.Properties
            .Where(property => !NotSnapshotted.Contains(property.Metadata.Name))
            .OrderBy(property => property.Metadata.Name, StringComparer.Ordinal);

        foreach (var property in snapshotted)
        {
            var name = property.Metadata.Name;
            var sensitive = IsSensitive(type, name);

            // Whether the COLUMN is jsonb, read from the model — the only place the
            // answer is knowable, and never guessed from the value.
            var storedAsJson = string.Equals(
                property.Metadata.GetColumnType(), "jsonb", StringComparison.OrdinalIgnoreCase);

            // The converter, so the snapshot records WHAT THE COLUMN HOLDS rather than
            // what the CLR object looks like. `Property.CurrentValue` is the model value,
            // and for a converted property the two are different objects with different
            // shapes — measured, and the difference is not cosmetic: a `LocalizedText`
            // display name stores {"en":"Vocabulary Card","tr":"Kelime Kartı"} and
            // serialises from its CLR side as {"Locales":["en","tr"]}, which records which
            // languages exist and none of the text. Every display name in Customization —
            // the tenant-authored values that module exists for — would have been audited
            // as a list of locale codes, and the row is append-only so nothing could
            // recover the words afterwards.
            var converter = property.Metadata.GetValueConverter();

            var beforeJson = entry.State == EntityState.Added
                ? null
                : Value(Stored(converter, property.OriginalValue), sensitive, storedAsJson);

            var afterJson = entry.State == EntityState.Deleted
                ? null
                : Value(Stored(converter, property.CurrentValue), sensitive, storedAsJson);

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

    /// <summary>
    /// The value as the column holds it, through the property's converter when it has one.
    /// </summary>
    /// <remarks>
    /// Three shipped shapes go through here and each lands right only because of it: a
    /// Vogen identifier becomes its <c>Guid</c>, an enum mapped by
    /// <c>HasEnumAsText()</c> becomes the member name the column stores, and a
    /// <c>LocalizedText</c> becomes the JSON document its <c>jsonb</c> column holds. An
    /// audit row that recorded the CLR side would disagree with the table it describes,
    /// and a reader has no way to tell which of the two is the record.
    /// </remarks>
    private static object? Stored(ValueConverter? converter, object? value) =>
        converter is null || value is null ? value : converter.ConvertToProvider(value);

    private static string Value(object? value, bool sensitive, bool storedAsJson) =>
        sensitive
            ? AuditJson.Quote(SensitiveTokenCatalog.RedactedValue)
            : AuditJson.Render(value, storedAsJson);

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
                return IsMarked(type, name) || SensitiveTokenCatalog.IsSensitive(name);
            });

    /// <summary>
    /// Whether the property carries <see cref="PiiSensitiveAttribute"/>, wherever in the
    /// hierarchy it was declared.
    /// </summary>
    /// <remarks>
    /// The walk is not decoration. <c>Type.GetProperty</c> with
    /// <c>BindingFlags.NonPublic</c> searches the given type only — a <b>private</b>
    /// property declared on a base class is invisible to it, and this repository's
    /// aggregates put exactly that shape on <c>AuditableEntity&lt;TId&gt;</c>. A marker
    /// the lookup cannot see is a marker that silently does nothing, which is the worst
    /// failure a redaction gate has: it reports success and writes the value.
    /// <c>Inherited = true</c> on the attribute does not help, because the problem is
    /// finding the <c>PropertyInfo</c> rather than reading its attributes.
    /// </remarks>
    private static bool IsMarked(Type? type, string name)
    {
        const BindingFlags Flags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance
            | BindingFlags.DeclaredOnly;

        for (var current = type; current is not null; current = current.BaseType)
        {
            var property = current.GetProperty(name, Flags);

            if (property?.GetCustomAttribute<PiiSensitiveAttribute>() is not null)
            {
                return true;
            }
        }

        return false;
    }
}
