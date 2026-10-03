using LearnStack.SharedKernel.Results;

namespace LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;

/// <summary>Live public scope and locale configuration on the announced read-only transaction.</summary>
public interface IPublicTenantConfigurationReader
{
    Task<Result<PublicTenantConfiguration>> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>Valid emptiness is distinct from an unavailable scope or invalid stored configuration.</summary>
public sealed record PublicTenantConfiguration(
    string DisplayName, IReadOnlyList<string> EnabledLocales, string? DefaultLocale);
