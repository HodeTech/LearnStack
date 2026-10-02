namespace LearnStack.Modules.Tenancy.Application.Abstractions;

/// <summary>Checks that the announced tenant exists and is not soft-deleted.</summary>
public interface ITenantExistenceReader
{
    Task<bool> ExistsAsync(CancellationToken cancellationToken);
}
