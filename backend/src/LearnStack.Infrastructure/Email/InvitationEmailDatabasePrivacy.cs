using LearnStack.SharedKernel.Persistence;
using Npgsql;

namespace LearnStack.Infrastructure.Email;

/// <summary>Actual server settings, including row DETAIL and sampled statements, precede invitation staging.</summary>
public sealed class InvitationEmailDatabasePrivacy(IUnitOfWork unit)
{
    public async Task RequireAsync(CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await using var command = new NpgsqlCommand(QualificationSql, (NpgsqlConnection)unit.Connection,
                (NpgsqlTransaction?)unit.Transaction)
            { CommandTimeout = 5 };
            if (await command.ExecuteScalarAsync(budget.Token).ConfigureAwait(false) is not true)
                throw new InvalidOperationException("Invitation database privacy qualification refused.");
        }
        catch { unit.MarkRollbackOnly(); throw new InvalidOperationException("Invitation database privacy qualification refused."); }
    }

    internal const string QualificationSql = """
        SELECT current_setting('log_error_verbosity')='terse'
          AND current_setting('log_min_error_statement')='panic'
          AND current_setting('log_statement')='none'
          AND current_setting('log_min_duration_statement')='-1'
          AND current_setting('log_min_duration_sample')='-1'
          AND current_setting('log_transaction_sample_rate')::numeric=0
          AND current_setting('log_parameter_max_length')='0'
          AND current_setting('log_parameter_max_length_on_error')='0'
        """;
}
