using LearnStack.Modules.Customization.Domain;

namespace LearnStack.Modules.Customization.Application.Abstractions;

/// <summary>Detached retained own-module history and ordered target preparation.</summary>
public interface ISeedCustomizationState
{
    Task PrepareAsync(bool taxonomy, string key, Guid? rootId, bool registration, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantContentType>> ReadContentTypesAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantLevelTaxonomy>> ReadTaxonomiesAsync(CancellationToken cancellationToken);
}
