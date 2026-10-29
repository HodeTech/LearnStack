using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Errors;

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

/// <summary>A fixed owning-port failure; provider text and inner exceptions never leave the reader.</summary>
public sealed class TenantLocaleEligibilityReadException()
    : InfrastructureException("Tenant locale eligibility is unavailable.");
