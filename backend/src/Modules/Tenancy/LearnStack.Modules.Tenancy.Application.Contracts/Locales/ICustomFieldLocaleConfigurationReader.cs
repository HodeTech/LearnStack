using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Locales;

public sealed record CustomFieldLocaleConfiguration(string DefaultLocale, IReadOnlyList<string> EnabledLocales);

/// <summary>Reads the current tenant's enabled authoring locales on the ambient owner.</summary>
public interface ICustomFieldLocaleConfigurationReader
{
    Task<Result<CustomFieldLocaleConfiguration>> ReadAsync(CancellationToken cancellationToken = default);
}
