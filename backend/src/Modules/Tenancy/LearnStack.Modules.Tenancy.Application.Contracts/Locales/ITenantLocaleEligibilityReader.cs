using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Locales;

/// <summary>
/// Reads canonical enabled membership in the announced tenant on the ambient
/// transaction, without caching, fallback or an implicit default language.
/// Invalid stored configuration is a bounded validation refusal (P02d-2).
/// </summary>
public interface ITenantLocaleEligibilityReader
{
    Task<Result<string>> ReadEligibleAsync(string locale, CancellationToken cancellationToken);
}
