using System.Globalization;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.Logging;

namespace LearnStack.Modules.Customization.Infrastructure.Projections;

/// <summary>Awaited get/set only; an ambient loader never enters a shared factory flight.</summary>
public sealed class DefinitionFamilyCache(
    ICacheService cache, CustomizationReadState state, IUnitOfWork unit, ILogger<DefinitionFamilyCache> logger)
{
    private static readonly CacheOptions Options = new(TimeSpan.FromSeconds(60), TimeSpan.FromMinutes(15));
    private bool CanUse => !state.IsDirty && !unit.IsRollbackOnly;

    internal async Task<DefinitionSnapshot?> ReadAsync(TenantId tenant, long generation, CancellationToken cancellationToken)
    {
        if (!CanUse) return null;
        try
        {
            var types = await cache.GetAsync<ContentTypeFamily>(Key(tenant, "content-types", generation), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanUse) return null;
            var taxonomies = await cache.GetAsync<TaxonomyFamily>(Key(tenant, "taxonomies", generation), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return CanUse && types is not null && taxonomies is not null
                ? new DefinitionSnapshot(generation, types.Definitions.Count > 0 || taxonomies.Definitions.Count > 0, types, taxonomies)
                : null;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectionCacheLog.ReadFailed(logger);
            return null;
        }
    }

    internal async Task WriteAsync(TenantId tenant, DefinitionSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!CanUse || snapshot.Generation is not { } generation) return;
        try
        {
            await cache.SetAsync(Key(tenant, "content-types", generation), snapshot.ContentTypes, Options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!CanUse) return;
            await cache.SetAsync(Key(tenant, "taxonomies", generation), snapshot.Taxonomies, Options, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProjectionCacheLog.WriteFailed(logger);
        }
    }

    private static string Key(TenantId tenant, string family, long generation) =>
        CacheKey.ForTenant(tenant.Value, "customization", family, "v" + generation.ToString(CultureInfo.InvariantCulture));
}

internal static partial class ProjectionCacheLog
{
    // Deliberately no exception/message/key payload: faults may carry private provider data.
    [LoggerMessage(EventId = 7401, Level = LogLevel.Warning, Message = "Customization cache read failed; loading the database snapshot.")]
    internal static partial void ReadFailed(ILogger logger);

    [LoggerMessage(EventId = 7402, Level = LogLevel.Warning, Message = "Customization cache write failed; returning the database snapshot.")]
    internal static partial void WriteFailed(ILogger logger);
}
