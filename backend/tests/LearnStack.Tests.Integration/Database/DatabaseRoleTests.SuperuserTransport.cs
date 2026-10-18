using System.Transactions;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class DatabaseRoleTests
{
    [Fact]
    public async Task Superuser_transport_uses_the_container_identity_and_closes_each_physical_session()
    {
        // Closed-alphabet identifiers originate here, never in caller input.
        var table = "superuser_transport_" + Guid.NewGuid().ToString("N");
        try
        {
            await _postgres.ExecuteAsSuperuserAsync($"""
                CREATE TABLE public.{table} (
                    ordinal int PRIMARY KEY, backend_pid int NOT NULL,
                    session_identity text NOT NULL, current_identity text NOT NULL,
                    database_name text NOT NULL, marker text NOT NULL);
                GRANT SELECT ON public.{table} TO learnstack_app;
                DO $body$ BEGIN PERFORM 'literal;semicolon'; END $body$;
                INSERT INTO public.{table}
                SELECT 1, pg_backend_pid(), session_user, current_user, current_database(), 'first;value';
                """);
            await _postgres.ExecuteAsSuperuserAsync($"""
                INSERT INTO public.{table}
                SELECT 2, pg_backend_pid(), session_user, current_user, current_database(), 'second;value';
                """);

            await using var observer = new NpgsqlConnection(SuperuserTransportObserverConnectionString());
            await observer.OpenAsync();
            await using var read = new NpgsqlCommand($"""
                SELECT backend_pid, session_identity, current_identity, database_name, marker,
                    (SELECT rolsuper FROM pg_roles WHERE rolname = session_identity)
                FROM public.{table} ORDER BY ordinal
                """, observer);
            var pids = new List<int>();
            var markers = new List<string>();
            await using (var rows = await read.ExecuteReaderAsync())
            {
                while (await rows.ReadAsync())
                {
                    pids.Add(rows.GetInt32(0));
                    rows.GetString(1).Should().Be("postgres");
                    rows.GetString(2).Should().Be("postgres");
                    rows.GetString(3).Should().Be("learnstack");
                    markers.Add(rows.GetString(4));
                    rows.GetBoolean(5).Should().BeTrue();
                }
            }

            markers.Should().Equal("first;value", "second;value");
            pids.Should().HaveCount(2).And.OnlyHaveUniqueItems();
            await AssertSuperuserTransportBackendsExitedAsync(observer, pids.ToArray());
        }
        finally
        {
            await _postgres.ExecuteAsSuperuserAsync($"DROP TABLE IF EXISTS public.{table}");
        }
    }

    [Fact]
    public async Task Superuser_transport_creates_and_drops_a_database_outside_an_aborted_ambient_transaction()
    {
        var database = "superuser_transport_" + Guid.NewGuid().ToString("N");
        try
        {
            // CREATE/DROP DATABASE cannot run inside a transaction. Neither
            // helper call may enlist, and disposing this scope must not undo it.
            using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await _postgres.ExecuteAsSuperuserAsync($"CREATE DATABASE {database} OWNER learnstack_migration");
            }

            await using var observer = new NpgsqlConnection(SuperuserTransportObserverConnectionString());
            await observer.OpenAsync();
            await using var owner = new NpgsqlCommand(
                "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @database", observer);
            owner.Parameters.AddWithValue("database", database);
            (await owner.ExecuteScalarAsync()).Should().Be("learnstack_migration");

            var targetString = new NpgsqlConnectionStringBuilder(SuperuserTransportObserverConnectionString())
            {
                Database = database,
            }.ConnectionString;
            await using (var target = new NpgsqlConnection(targetString))
            {
                await target.OpenAsync();
                await using var identity = new NpgsqlCommand("SELECT session_user, current_user, current_database()", target);
                await using var row = await identity.ExecuteReaderAsync();
                (await row.ReadAsync()).Should().BeTrue();
                row.GetString(0).Should().Be("learnstack_app");
                row.GetString(1).Should().Be("learnstack_app");
                row.GetString(2).Should().Be(database);
                (await row.ReadAsync()).Should().BeFalse();
            }

            using (var ambient = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
            {
                await _postgres.ExecuteAsSuperuserAsync($"DROP DATABASE {database} WITH (FORCE)");
            }

            (await owner.ExecuteScalarAsync()).Should().BeNull("the committed database drop survives the aborted scope");
        }
        finally
        {
            await _postgres.ExecuteAsSuperuserAsync($"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
        }
    }

    [Theory]
    [InlineData(false, "22012")]
    [InlineData(true, "57014")]
    public async Task Superuser_transport_rolls_back_the_failed_batch_and_closes_its_observed_backend(
        bool serverCancellation, string sqlState)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sequence = "transport_pid_" + suffix;
        var before = "transport_before_" + suffix;
        var after = "transport_after_" + suffix;
        var passwordCanary = "private_" + suffix;
        var key = BitConverter.ToInt64(Guid.NewGuid().ToByteArray(), 0);
        var failureSql = serverCancellation
            ? "SET statement_timeout = '50ms'; SELECT pg_sleep(5);"
            : "SELECT 1 / 0;";
        try
        {
            // setval is visible immediately and survives transaction rollback:
            // it gives the app observer the actual PID even when this batch fails.
            await _postgres.ExecuteAsSuperuserAsync($"""
                CREATE SEQUENCE public.{sequence} MINVALUE 0 START WITH 0;
                GRANT SELECT ON SEQUENCE public.{sequence} TO learnstack_app;
                """);
            await using var observer = new NpgsqlConnection(SuperuserTransportObserverConnectionString());
            await observer.OpenAsync();
            await using var barrier = new NpgsqlConnection(SuperuserTransportObserverConnectionString());
            Task? batch = null;
            Exception? failure = null;
            var pid = 0;
            try
            {
                await barrier.OpenAsync();
                await using var hold = new NpgsqlCommand("SELECT pg_advisory_lock(@key)", barrier);
                hold.Parameters.AddWithValue("key", key);
                await hold.ExecuteNonQueryAsync();
                batch = _postgres.ExecuteAsSuperuserAsync($"""
                    SELECT setval('public.{sequence}', pg_backend_pid(), false);
                    CREATE ROLE {before} NOLOGIN PASSWORD '{passwordCanary}';
                    SELECT pg_advisory_xact_lock({key.ToString(System.Globalization.CultureInfo.InvariantCulture)});
                    {failureSql}
                    CREATE ROLE {after} NOLOGIN;
                    """);

                using var observed = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await using var witness = new NpgsqlCommand($"SELECT last_value FROM public.{sequence}", observer);
                while (pid == 0)
                {
                    pid = checked((int)(long)(await witness.ExecuteScalarAsync(observed.Token))!);
                    if (pid == 0) await Task.Delay(10, observed.Token);
                }

                await using var live = new NpgsqlCommand("""
                    SELECT EXISTS (SELECT 1 FROM pg_stat_activity
                        WHERE pid = @pid AND usename = 'postgres' AND datname = 'learnstack')
                    """, observer);
                live.Parameters.AddWithValue("pid", pid);
                (await live.ExecuteScalarAsync(observed.Token)).Should().Be(true,
                    "the app observer must see this exact live backend before its absence can prove cleanup");
            }
            finally
            {
                try
                {
                    // Pooling=false physically closes the owner and releases the
                    // session lock before joining the batch on every test path.
                    await barrier.DisposeAsync();
                }
                finally
                {
                    if (batch is not null) failure = await Record.ExceptionAsync(() => batch);
                }
            }

            var rejected = failure.Should().BeOfType<InvalidOperationException>().Which;
            rejected.Message.Should().Contain($"SQLSTATE {sqlState}");
            rejected.ToString().Should().NotContain(passwordCanary);
            await AssertSuperuserTransportBackendsExitedAsync(observer, [pid]);
            await using var roles = new NpgsqlCommand(
                "SELECT count(*) FROM pg_roles WHERE rolname = ANY(@names)", observer);
            roles.Parameters.AddWithValue("names", new[] { before, after });
            (await roles.ExecuteScalarAsync()).Should().Be(0L,
                "the first CREATE rolls back and the CREATE after the failure never takes effect");

            // A healthy subsequent call still commits both statements.
            await _postgres.ExecuteAsSuperuserAsync($"CREATE ROLE {before} NOLOGIN; CREATE ROLE {after} NOLOGIN;");
            (await roles.ExecuteScalarAsync()).Should().Be(2L);
        }
        finally
        {
            try
            {
                await _postgres.ExecuteAsSuperuserAsync($"DROP ROLE IF EXISTS {before}, {after}");
            }
            finally
            {
                await _postgres.ExecuteAsSuperuserAsync($"DROP SEQUENCE IF EXISTS public.{sequence}");
            }
        }
    }

    private string SuperuserTransportObserverConnectionString() =>
        new NpgsqlConnectionStringBuilder(_postgres.AppConnectionString)
        {
            Pooling = false,
            Enlist = false,
            Timeout = 5,
            CommandTimeout = 5,
        }.ConnectionString;

    private static async Task AssertSuperuserTransportBackendsExitedAsync(NpgsqlConnection observer, int[] pids)
    {
        // Each query runs in autocommit, so no transaction retains a stale stats
        // snapshot. Poll shutdown propagation, never sleep for the helper budget.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var read = new NpgsqlCommand(
            "SELECT count(*) FROM pg_stat_activity WHERE pid = ANY(@pids)", observer);
        read.Parameters.AddWithValue("pids", pids);
        while ((long)(await read.ExecuteScalarAsync(deadline.Token))! != 0)
        {
            await Task.Delay(10, deadline.Token);
        }
    }
}
