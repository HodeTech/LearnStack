using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Customization.Application.Abstractions;

public sealed record CustomFieldDefinitionUsage(int Concepts, int Revisions, int Drafts, int LatestSchemaVersion);

/// <summary>One aggregate write boundary; retained revisions include discarded drafts.</summary>
public interface ICustomFieldDefinitionStore : IAggregateWriteStore<TenantCustomFieldDef, TenantCustomFieldDefId>
{
    Task<CustomFieldDefinitionUsage> ReadUsageAsync(string key, CancellationToken cancellationToken);
    Task<TenantCustomFieldDef?> FindAsync(TenantCustomFieldDefId id, CancellationToken cancellationToken);
    Task<bool> HasPublishedSuccessorAsync(string key, int schemaVersion, CancellationToken cancellationToken);
    Task<TenantCustomFieldDef?> FindActiveAsync(string key, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantCustomFieldDef>> ReadActiveAsync(CancellationToken cancellationToken);
    Task<CustomFieldDefinitionDto?> ReadAsync(Guid id, CancellationToken cancellationToken);
    Task<CustomFieldConceptPage> ListConceptsAsync(string? cursor, string? status, CancellationToken cancellationToken);
    Task<CustomFieldDefinitionPage> ListRevisionsAsync(string key, string? cursor, CancellationToken cancellationToken);
}
