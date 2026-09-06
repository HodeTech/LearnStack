using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace LearnStack.Modules.Customization.Infrastructure.Persistence;

/// <summary>
/// Advances a tenant's customization generation in one statement.
/// </summary>
/// <remarks>
/// <para>
/// <b>Raw SQL rather than change tracking, and
/// <see href="../../../../../../docs/decisions/0043-customization-payload-validation.md">ADR-0043
/// § 7</see> says why.</b> The row does not exist for a tenant that has never had
/// a customization, so a tracked update has nothing to update; and a
/// read-modify-write loses one of two concurrent bumps, which leaves every stale
/// key the lost one was meant to strand still reachable. <c>ON CONFLICT … DO
/// UPDATE</c> is one statement and both cases at once.
/// </para>
/// <para>
/// <b>It runs on the ambient connection.</b> <c>Database.GetDbConnection()</c>
/// returns the connection <c>IUnitOfWork</c> opened and enlisted this context on
/// (<see href="../../../../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>),
/// and <c>Database.CurrentTransaction</c> is the business transaction — so the
/// bump commits with the customization write or not at all, and it executes while
/// <c>app.tenant_id</c> is set, which the row's own <c>WITH CHECK</c> requires.
/// Opening a connection here instead would see neither.
/// </para>
/// <para>
/// <b>The increment reads the stored value, not a parameter.</b>
/// <c>generation = customization_generations.generation + 1</c> is evaluated by
/// PostgreSQL against the row it is about to write, under the row lock the
/// conflicting insert already took — so two concurrent bumps serialize and both
/// count. A parameter computed in C# would be the read-modify-write this exists
/// to avoid, spelled differently.
/// </para>
/// </remarks>
public sealed class CustomizationGenerationStore(CustomizationDbContext db)
    : ICustomizationGenerationStore
{
    private const string BumpSql =
        """
        INSERT INTO customization_generations (tenant_id, generation)
        VALUES (@tenant_id, 1)
        ON CONFLICT (tenant_id)
        DO UPDATE SET generation = customization_generations.generation + 1
        RETURNING generation
        """;

    public async Task<long> BumpAsync(
        TenantId tenantId, CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();

        await using var command = new NpgsqlCommand(BumpSql, connection)
        {
            Transaction = (NpgsqlTransaction?)db.Database.CurrentTransaction?.GetDbTransaction(),
        };

        command.Parameters.AddWithValue("tenant_id", tenantId.Value);

        var generation = await command.ExecuteScalarAsync(cancellationToken);

        // Null means the statement returned no row, which neither arm of this
        // statement can produce — the INSERT writes or the DO UPDATE writes, and
        // there is no WHERE on either to skip. So there is no state this code can
        // name, and the one thing that must not happen is falling back to a number:
        // a silent 0 would hand every subsequent reader a cache key composed against
        // a generation the write never advanced, so the stale entry stays reachable
        // for as long as it lives.
        return generation is null
            ? throw new InvalidOperationException(
                "The generation bump returned no row for tenant "
                + tenantId.Value
                + ". An upsert with no WHERE on either arm returned nothing, which is "
                + "a state this code cannot explain and must not paper over.")
            : (long)generation;
    }
}
