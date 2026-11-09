using System.Globalization;
using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using static LearnStack.Infrastructure.Persistence.WriteStoreTracking;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

public sealed class CustomFieldDefinitionStore(CustomizationDbContext db, IUnitOfWork unit) : ICustomFieldDefinitionStore
{
    private static readonly IReadOnlySet<string> OwnedConstraints = new HashSet<string>(StringComparer.Ordinal)
    {
        "pk_tenant_custom_field_defs", "ux_tenant_custom_field_defs_tenant_target_key_version",
        "ux_tenant_custom_field_defs_tenant_target_key_active",
    };
    public Task<CustomFieldDefinitionUsage> ReadUsageAsync(string key, CancellationToken cancellationToken) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => ReadUsageCoreAsync(key, cancellationToken));
    private async Task<CustomFieldDefinitionUsage> ReadUsageCoreAsync(string key, CancellationToken cancellationToken)
    {
        var rows = db.TenantCustomFieldDefinitions.Where(row => row.TargetEntity == CustomFieldTarget.Membership);
        return new(await rows.Select(row => row.Key).Distinct().CountAsync(cancellationToken), await rows.CountAsync(cancellationToken),
            await rows.CountAsync(row => row.Status == CustomizationStatus.Draft && row.DeletedAt == null, cancellationToken),
            await rows.Where(row => row.Key == key).Select(row => (int?)row.SchemaVersion).MaxAsync(cancellationToken) ?? 0);
    }
    public Task<TenantCustomFieldDef?> FindAsync(TenantCustomFieldDefId id, CancellationToken cancellationToken) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit,
            () => db.TenantCustomFieldDefinitions.SingleOrDefaultAsync(row => row.Id == id, cancellationToken));
    public Task<bool> HasPublishedSuccessorAsync(string key, int schemaVersion, CancellationToken cancellationToken) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => db.TenantCustomFieldDefinitions.AnyAsync(
            row => row.TargetEntity == CustomFieldTarget.Membership && row.Key == key
                && row.SchemaVersion > schemaVersion && row.Status != CustomizationStatus.Draft, cancellationToken));
    public Task<TenantCustomFieldDef?> FindActiveAsync(string key, CancellationToken cancellationToken) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => db.TenantCustomFieldDefinitions.SingleOrDefaultAsync(
            row => row.Key == key && row.TargetEntity == CustomFieldTarget.Membership
                && row.Status == CustomizationStatus.Active && row.DeletedAt == null, cancellationToken));
    public Task<IReadOnlyList<TenantCustomFieldDef>> ReadActiveAsync(CancellationToken cancellationToken) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => ReadActiveCoreAsync(cancellationToken));
    private async Task<IReadOnlyList<TenantCustomFieldDef>> ReadActiveCoreAsync(CancellationToken cancellationToken) =>
        await db.TenantCustomFieldDefinitions.AsNoTracking().Where(row => row.TargetEntity == CustomFieldTarget.Membership
            && row.Status == CustomizationStatus.Active && row.DeletedAt == null).OrderBy(row => row.Key).Take(21).ToListAsync(cancellationToken);
    public Task AddAsync(TenantCustomFieldDef definition, CancellationToken cancellationToken)
        => CustomFieldDefinitionStorageBoundary.WriteAsync(db, unit, () =>
        { db.TenantCustomFieldDefinitions.Add(definition); return SaveTranslatingConflictsAsync(db, cancellationToken, OwnedConstraints); });
    public Task UpdateAsync(TenantCustomFieldDef definition, CancellationToken cancellationToken)
        => CustomFieldDefinitionStorageBoundary.WriteAsync(db, unit, () =>
        { EnsureTracked(db, definition); return SaveTranslatingConflictsAsync(db, cancellationToken, OwnedConstraints); });
    public Task<CustomFieldDefinitionDto?> ReadAsync(Guid id, CancellationToken cancellationToken) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => ReadCoreAsync(id, cancellationToken));
    private async Task<CustomFieldDefinitionDto?> ReadCoreAsync(Guid id, CancellationToken cancellationToken)
    {
        var typed = TenantCustomFieldDefId.From(id);
        var row = await db.TenantCustomFieldDefinitions.AsNoTracking().SingleOrDefaultAsync(row => row.Id == typed, cancellationToken);
        return row is null ? null : CustomFieldDefinitionProjection.ToDto(row);
    }
    public Task<CustomFieldConceptPage> ListConceptsAsync(string? cursor, string? status, CancellationToken cancellationToken) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => ListConceptsCoreAsync(cursor, status, cancellationToken));
    private async Task<CustomFieldConceptPage> ListConceptsCoreAsync(string? cursor, string? status, CancellationToken cancellationToken)
    {
        if (status is not (null or "Draft" or "Active" or "Deprecated")) throw new ArgumentException("Definition status is invalid.", nameof(status));
        var cursorKind = "concepts-" + (status ?? "all");
        var after = cursor is null ? null : Decode(cursor, cursorKind, out _);
        var rows = db.TenantCustomFieldDefinitions.AsNoTracking().Where(row => row.TargetEntity == CustomFieldTarget.Membership);
        if (status is not null)
        {
            var wanted = Enum.Parse<CustomizationStatus>(status);
            rows = rows.Where(row => db.TenantCustomFieldDefinitions.Any(candidate => candidate.TargetEntity == CustomFieldTarget.Membership
                && candidate.Key == row.Key && candidate.Status == wanted));
        }
        // Concept summaries have a hard retained ceiling of 100, independent of revision history.
        // Compare the bounded projection ordinally instead of depending on the database locale.
        var retained = await rows.GroupBy(row => row.Key).OrderBy(group => group.Key)
            .Select(group => new { Key = group.Key, Latest = group.Max(row => row.SchemaVersion) }).Take(101).ToListAsync(cancellationToken);
        if (retained.Count > 100) throw new InvalidOperationException("Definition concept capacity is invalid.");
        var page = retained.Where(row => after is null || string.Compare(row.Key, after, StringComparison.Ordinal) > 0)
            .OrderBy(row => row.Key, StringComparer.Ordinal).Take(21).ToList();
        var keys = page.Take(20).Select(row => row.Key).ToArray();
        var active = await db.TenantCustomFieldDefinitions.AsNoTracking().Where(row => keys.Contains(row.Key)
            && row.TargetEntity == CustomFieldTarget.Membership && row.Status == CustomizationStatus.Active && row.DeletedAt == null)
            .ToListAsync(cancellationToken);
        var summaries = page.Take(20).Select(row => new CustomFieldConceptSummary(row.Key, row.Latest,
            active.SingleOrDefault(item => item.Key == row.Key)?.Id.Value,
            active.SingleOrDefault(item => item.Key == row.Key)?.SchemaVersion)).ToArray();
        return new(summaries, page.Count > 20 ? Encode(cursorKind, page[19].Key, 0) : null);
    }
    public Task<CustomFieldDefinitionPage> ListRevisionsAsync(string key, string? cursor, CancellationToken cancellationToken) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => ListRevisionsCoreAsync(key, cursor, cancellationToken));
    private async Task<CustomFieldDefinitionPage> ListRevisionsCoreAsync(string key, string? cursor, CancellationToken cancellationToken)
    {
        var after = 0;
        if (cursor is not null && Decode(cursor, "revisions", out after) != key) throw InvalidCursor();
        var rows = await db.TenantCustomFieldDefinitions.AsNoTracking().Where(row => row.TargetEntity == CustomFieldTarget.Membership
            && row.Key == key && row.SchemaVersion > after).OrderBy(row => row.SchemaVersion).Take(21).ToListAsync(cancellationToken);
        return new(rows.Take(20).Select(CustomFieldDefinitionProjection.ToDto).ToArray(), rows.Count > 20 ? Encode("revisions", key, rows[19].SchemaVersion) : null);
    }
    private static string Encode(string kind, string key, int version) => Convert.ToBase64String(Encoding.UTF8.GetBytes($"1|{kind}|{key}|{version.ToString(CultureInfo.InvariantCulture)}"));
    private static string Decode(string cursor, string kind, out int version)
    {
        version = 0;
        if (cursor.Length > 256) throw InvalidCursor();
        try
        {
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            var parts = text.Split('|');
            if (parts.Length != 4 || parts[0] != "1" || parts[1] != kind || !Application.CustomFields.CustomFieldFormProfile.ValidKey(parts[2])
                || !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out version) || version < 0
                || kind.StartsWith("concepts-", StringComparison.Ordinal) && version != 0 || kind == "revisions" && version == 0
                || Encode(kind, parts[2], version) != cursor) throw InvalidCursor();
            return parts[2];
        }
        catch (FormatException) { throw InvalidCursor(); }
    }
    private static ArgumentException InvalidCursor() => new("Definition cursor is invalid.");
}

public sealed class ExactCustomFieldDefinitionReader(CustomizationDbContext db, ITenantContext context, IIdentityMutationLocks locks, IUnitOfWork unit)
    : IExactCustomFieldDefinitionReader
{
    public async Task<CustomFieldDefinitionDto?> ReadAsync(CustomFieldDefinitionPin pin, CustomFieldResolution resolution,
        IdentityMutationLockPlan? fence, CancellationToken cancellationToken = default) =>
        (await ReadBatchAsync([pin], resolution, fence, cancellationToken)).SingleOrDefault();

    public Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadBatchAsync(IReadOnlyCollection<CustomFieldDefinitionPin> pins,
        CustomFieldResolution resolution, IdentityMutationLockPlan? fence, CancellationToken cancellationToken = default) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => ReadBatchCoreAsync(pins, resolution, fence, cancellationToken));
    private async Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadBatchCoreAsync(IReadOnlyCollection<CustomFieldDefinitionPin> pins,
        CustomFieldResolution resolution, IdentityMutationLockPlan? fence, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pins);
        if (!Enum.IsDefined(resolution) || pins.Count > 20 || pins.Any(pin => pin is null || pin.DefinitionId == Guid.Empty || pin.SchemaVersion < 1)
            || pins.Select(pin => pin.DefinitionId).Distinct().Count() != pins.Count) throw new ArgumentException("Definition pins are invalid.", nameof(pins));
        if (resolution == CustomFieldResolution.NewBinding) RequireFence(fence);
        var ids = pins.Select(pin => TenantCustomFieldDefId.From(pin.DefinitionId)).ToArray();
        var query = db.TenantCustomFieldDefinitions.AsNoTracking().Where(row => ids.Contains(row.Id)
            && row.TargetEntity == CustomFieldTarget.Membership && row.DeletedAt == null);
        query = resolution == CustomFieldResolution.NewBinding ? query.Where(row => row.Status == CustomizationStatus.Active)
            : query.Where(row => row.Status == CustomizationStatus.Active || row.Status == CustomizationStatus.Deprecated);
        var rows = await query.ToListAsync(cancellationToken);
        if (resolution == CustomFieldResolution.NewBinding) RequireFence(fence);
        return rows.Where(row => pins.Any(pin => pin.DefinitionId == row.Id.Value && pin.SchemaVersion == row.SchemaVersion))
            .OrderBy(row => row.Key, StringComparer.Ordinal).Select(CustomFieldDefinitionProjection.ToDto).ToArray();
    }
    public Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadActiveAsync(IdentityMutationLockPlan fence, CancellationToken cancellationToken = default) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => ReadActiveCoreAsync(fence, cancellationToken));
    private async Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadActiveCoreAsync(IdentityMutationLockPlan fence, CancellationToken cancellationToken)
    {
        RequireFence(fence);
        var rows = await db.TenantCustomFieldDefinitions.AsNoTracking().Where(row => row.TargetEntity == CustomFieldTarget.Membership
            && row.Status == CustomizationStatus.Active && row.DeletedAt == null).OrderBy(row => row.Sort).ThenBy(row => row.Key).Take(21).ToListAsync(cancellationToken);
        RequireFence(fence);
        if (rows.Count > 20) throw new InvalidOperationException("Active definition capacity is invalid.");
        return rows.Select(CustomFieldDefinitionProjection.ToDto).ToArray();
    }
    public Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadActiveForDisplayAsync(CancellationToken cancellationToken = default) =>
        CustomFieldDefinitionStorageBoundary.ReadAsync(db, unit, () => ReadActiveForDisplayCoreAsync(cancellationToken));
    private async Task<IReadOnlyList<CustomFieldDefinitionDto>> ReadActiveForDisplayCoreAsync(CancellationToken cancellationToken)
    {
        var rows = await db.TenantCustomFieldDefinitions.AsNoTracking().Where(row => row.TargetEntity == CustomFieldTarget.Membership
            && row.Status == CustomizationStatus.Active && row.DeletedAt == null).OrderBy(row => row.Sort).ThenBy(row => row.Key)
            .Take(21).ToListAsync(cancellationToken);
        if (rows.Count > 20) throw new InvalidOperationException("Active definition capacity is invalid.");
        return rows.Select(CustomFieldDefinitionProjection.ToDto).ToArray();
    }
    private void RequireFence(IdentityMutationLockPlan? fence)
    {
        if (fence is null || !context.IsResolved || fence.TenantId != context.TenantId) throw new InvalidOperationException("A current Tenant fence is required.");
        locks.RequireCurrent(fence);
    }
}

internal static class CustomFieldDefinitionProjection
{
    internal static CustomFieldDefinitionDto ToDto(TenantCustomFieldDef row) => new(row.Id.Value, row.TargetEntity.ToString(), row.Key,
        row.SchemaVersion, row.Status.ToString(), row.Version, row.DeletedAt is not null, row.JsonSchema,
        JsonSerializer.Deserialize<SortedDictionary<string, string>>(row.LabelsJson)!, row.PiiCategory, row.IsRequired, row.Sort,
        row.PurposeKey, row.NoticesJson is null ? null : JsonSerializer.Deserialize<SortedDictionary<string, string>>(row.NoticesJson), row.MaximumValueBytes,
        row.FormLabelsJson is null ? null : JsonSerializer.Deserialize<List<CustomFieldControlLabels>>(row.FormLabelsJson));
}
