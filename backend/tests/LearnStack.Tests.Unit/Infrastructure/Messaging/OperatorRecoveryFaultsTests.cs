using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Messaging;

/// <summary>
/// What an operator call reports as a retryable outage and what it leaves to surface as the defect it is
/// (ADR-0103 Amendment 1): a retry loop cannot cure a refused credential or a missing grant.
/// </summary>
public sealed class OperatorRecoveryFaultsTests
{
    private static PostgresException Server(string sqlState) => new("message", "ERROR", "ERROR", sqlState);

    [Theory]
    [InlineData(PostgresErrorCodes.QueryCanceled)]
    [InlineData(PostgresErrorCodes.LockNotAvailable)]
    [InlineData(PostgresErrorCodes.TooManyConnections)]
    [InlineData(PostgresErrorCodes.AdminShutdown)]
    public void A_server_fault_that_waiting_can_cure_is_transient(string sqlState) =>
        OperatorRecoveryFaults.IsTransientDatabaseFault(Server(sqlState), CancellationToken.None).Should().BeTrue(sqlState);

    [Theory]
    [InlineData(PostgresErrorCodes.InsufficientPrivilege)]
    [InlineData(PostgresErrorCodes.InvalidPassword)]
    [InlineData(PostgresErrorCodes.UndefinedTable)]
    [InlineData(PostgresErrorCodes.UndefinedColumn)]
    [InlineData(PostgresErrorCodes.InvalidAuthorizationSpecification)]
    public void A_refused_credential_or_a_missing_grant_or_table_is_a_defect_and_never_transient(string sqlState)
    {
        OperatorRecoveryFaults.IsTransientDatabaseFault(Server(sqlState), CancellationToken.None).Should().BeFalse(sqlState);
        OperatorRecoveryFaults.IsUnavailable(Server(sqlState), CancellationToken.None).Should().BeFalse(sqlState);
    }

    [Fact]
    public void An_unreachable_database_and_a_timeout_are_transient()
    {
        OperatorRecoveryFaults.IsTransientDatabaseFault(
            new NpgsqlException("unreachable", new IOException("reset")), CancellationToken.None).Should().BeTrue();
        OperatorRecoveryFaults.IsTransientDatabaseFault(new TimeoutException(), CancellationToken.None).Should().BeTrue();
    }

    [Fact]
    public void A_cancellation_is_an_outage_only_when_the_caller_did_not_ask_for_it()
    {
        using var asked = new CancellationTokenSource();
        asked.Cancel();

        OperatorRecoveryFaults.IsTransientDatabaseFault(new OperationCanceledException(), CancellationToken.None).Should().BeTrue();
        OperatorRecoveryFaults.IsTransientDatabaseFault(new OperationCanceledException(), asked.Token).Should().BeFalse();
    }

    [Fact]
    public void The_owners_own_refusals_are_unavailable_and_a_programmer_error_is_not()
    {
        OperatorRecoveryFaults.IsUnavailable(new InvalidOperationException("lane busy"), CancellationToken.None).Should().BeTrue();
        OperatorRecoveryFaults.IsUnavailable(new NotSupportedException("not configured"), CancellationToken.None).Should().BeFalse();
        OperatorRecoveryFaults.IsUnavailable(new ArgumentException("bad"), CancellationToken.None).Should().BeFalse();
    }
}
