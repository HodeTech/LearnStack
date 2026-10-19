using System.Data;
using System.Globalization;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class HangfireStorageTests
{
    [Theory]
    [InlineData(false, "idle in transaction")]
    [InlineData(true, "idle in transaction (aborted)")]
    public async Task Guarded_source_reaps_idle_and_aborted_transactions_then_reopens_a_healthy_connection(
        bool abort, string expectedState)
    {
        await using var source = Source();
        using var setupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        // The same role can see this session's state without pg_read_all_stats
        // or a fixture grant; learnstack_migration cannot see another role's state.
        await using var observer = await source.OpenConnectionAsync(setupDeadline.Token);
        await AssertDispatcherRoleAsync(observer, setupDeadline.Token);
        int process;
        await using (var connection = await source.OpenConnectionAsync(setupDeadline.Token))
        {
            process = connection.ProcessID;
            observer.ProcessID.Should().NotBe(process);
            await AssertDispatcherRoleAsync(connection, setupDeadline.Token);
            await AssertBudgetsAsync(connection);
            await using var transaction = await connection.BeginTransactionAsync(setupDeadline.Token);
            await using (var command = new NpgsqlCommand(abort ? "SELECT 1 / 0" : "SELECT 1", connection, transaction))
            {
                if (abort)
                {
                    var fail = async () => await command.ExecuteScalarAsync(setupDeadline.Token);
                    (await fail.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.DivisionByZero);
                }
                else
                {
                    (await command.ExecuteScalarAsync(setupDeadline.Token)).Should().Be(1);
                }
            }

            // Keep the transaction and its checked-out connection alive: disposal
            // must not be what removes the backend being measured.
            using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await WaitForBackendStateAsync(observer, process, expectedState, cleanupDeadline.Token);
            await WaitForBackendStateAsync(observer, process, expectedState: null, cleanupDeadline.Token);

            using var readDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var read = new NpgsqlCommand("SELECT 1", connection, transaction);
            var readClosedBackend = async () => await read.ExecuteScalarAsync(readDeadline.Token);
            await readClosedBackend.Should().ThrowAsync<NpgsqlException>();
            connection.State.Should().Be(ConnectionState.Closed);
        }

        // Disposing the dead transaction/connection above must leave the guarded
        // source able to acquire a fresh backend with its normal startup settings.
        using var recoveryDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var replacement = await source.OpenConnectionAsync(recoveryDeadline.Token);
        replacement.ProcessID.Should().NotBe(process);
        await AssertDispatcherRoleAsync(replacement, recoveryDeadline.Token);
        await AssertBudgetsAsync(replacement);
        (await ScalarAsync(replacement, "SELECT 1")).Should().Be(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5001)]
    public async Task Bootstrap_refuses_disabled_or_excessive_idle_startup_deadline_and_accepts_the_guarded_source(int idleMilliseconds)
    {
        await using var dirty = StartupDeadlineSource(idleMilliseconds);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using (var connection = await dirty.OpenConnectionAsync(deadline.Token))
        {
            // Establish that the planted idle setting is the defect: the real
            // physical session uses the correct role and finite other deadlines.
            await AssertDispatcherRoleAsync(connection, deadline.Token);
            (await ScalarAsync(connection, "SELECT current_setting('statement_timeout')")).Should().Be("5s");
            (await ScalarAsync(connection, "SELECT current_setting('lock_timeout')")).Should().Be("5s");
            (await ScalarAsync(connection, "SELECT setting FROM pg_settings WHERE name='idle_in_transaction_session_timeout'"))
                .Should().Be(idleMilliseconds.ToString(CultureInfo.InvariantCulture));
            connection.ConnectionTimeout.Should().Be(5);
            connection.CommandTimeout.Should().Be(5);
        }

        var create = () => HangfireStorageBootstrap.Create(dirty, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
        create.Should().Throw<HangfireStorageRefusedException>().Which.InnerException.Should().BeNull();

        await using var healthy = Source();
        var storage = HangfireStorageBootstrap.Create(healthy, BackgroundJobInfrastructureRegistry.CreateDefault(), Diagnostics());
        using var vendor = storage.GetConnection();
        vendor.Should().NotBeNull();
        using var healthyDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var sql = await healthy.OpenConnectionAsync(healthyDeadline.Token);
        await AssertDispatcherRoleAsync(sql, healthyDeadline.Token);
        await AssertBudgetsAsync(sql);
    }

    private PrivilegedDataSource StartupDeadlineSource(int idleMilliseconds) => new(() =>
    {
        // A real Npgsql source is needed to plant startup drift: the production
        // builder deliberately replaces caller-supplied startup options.
        var settings = new NpgsqlConnectionStringBuilder(schema.Postgres.OutboxConnectionString)
        {
            Options = $"-c statement_timeout=5000 -c lock_timeout=5000 -c idle_in_transaction_session_timeout={idleMilliseconds.ToString(CultureInfo.InvariantCulture)}",
            Timeout = 5,
            CommandTimeout = 5,
            CancellationTimeout = 1000,
            Enlist = false,
            Multiplexing = false,
            NoResetOnClose = false,
            IncludeErrorDetail = false,
            LogParameters = false,
            PersistSecurityInfo = false,
        };
        return new NpgsqlDataSourceBuilder(settings.ConnectionString).Build();
    });

    private static async Task AssertDispatcherRoleAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT session_user = @role AND current_user = @role", connection);
        command.Parameters.AddWithValue("role", OutboxDispatcherDataSource.Role);
        (await command.ExecuteScalarAsync(cancellationToken)).Should().Be(true);
    }

    private static async Task WaitForBackendStateAsync(
        NpgsqlConnection observer, int process, string? expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = new NpgsqlCommand("""
                SELECT state FROM pg_stat_activity
                WHERE pid = @process AND datname = current_database() AND usename = current_user
                """, observer);
            command.Parameters.AddWithValue("process", process);
            var state = await command.ExecuteScalarAsync(cancellationToken);
            if (expectedState is null ? state is null : state is string actual && actual == expectedState) return;
            await Task.Delay(25, cancellationToken);
        }
    }
}
