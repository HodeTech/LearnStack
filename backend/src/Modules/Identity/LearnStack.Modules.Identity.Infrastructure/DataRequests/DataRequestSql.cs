using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// The fixed parameterized statements of the data-request adapters: on the ambient transaction, bound to the announced
/// tenant and organization, under a five-second budget, with a provider failure leaving the owner rollback-only.
/// </summary>
internal static class DataRequestSql
{
    /// <summary>The one statement budget of the data-request adapters: the command timeout and the cancellation window.</summary>
    internal const int StatementBudgetSeconds = 5;

    /// <summary>A statement on the ambient transaction that binds no parameter, under the statement budget.</summary>
    internal static NpgsqlCommand Statement(IUnitOfWork unit, string sql) =>
        new(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!) { CommandTimeout = StatementBudgetSeconds };

    internal static NpgsqlCommand Command(IUnitOfWork unit, ITenantContext tenant, string sql)
    {
        var command = Statement(unit, sql);
        command.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)tenant.OrganizationId?.Value ?? DBNull.Value);
        return command;
    }

    /// <summary>A cancellation window of the statement budget, linked to the caller's token.</summary>
    internal static CancellationTokenSource Budget(CancellationToken cancellationToken)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(StatementBudgetSeconds));
        return budget;
    }

    internal static async Task<T> ExecuteAsync<T>(IUnitOfWork unit, Func<CancellationToken, Task<T>> statement,
        CancellationToken cancellationToken)
    {
        using var budget = Budget(cancellationToken);
        try
        {
            return await statement(budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request storage unavailable.");
        }
        catch (NpgsqlException)
        {
            unit.MarkRollbackOnly();
            throw new InfrastructureException("Data request storage unavailable.");
        }
    }
}
