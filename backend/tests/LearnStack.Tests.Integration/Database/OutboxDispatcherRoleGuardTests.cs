using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

// Role mutations belong to this class's container, never the shared schema suite.
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class OutboxDispatcherRoleGuardTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_actual_outbox_role_connects_with_its_exact_bounded_attributes()
    {
        await using var source = OutboxDispatcherDataSource.Build(postgres.OutboxConnectionString);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT session_user, current_user, rolbypassrls,
                rolsuper, rolcreatedb, rolcreaterole, rolreplication
            FROM pg_roles WHERE rolname = current_user
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();

        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(OutboxDispatcherDataSource.Role);
        reader.GetString(1).Should().Be(OutboxDispatcherDataSource.Role);
        reader.GetBoolean(2).Should().BeTrue();
        for (var ordinal = 3; ordinal <= 6; ordinal++) reader.GetBoolean(ordinal).Should().BeFalse();
        (await reader.ReadAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("NOBYPASSRLS", "BYPASSRLS", "NOT rolbypassrls", false)]
    [InlineData("NOBYPASSRLS", "BYPASSRLS", "NOT rolbypassrls", true)]
    [InlineData("SUPERUSER", "NOSUPERUSER", "rolsuper", false)]
    [InlineData("SUPERUSER", "NOSUPERUSER", "rolsuper", true)]
    [InlineData("CREATEDB", "NOCREATEDB", "rolcreatedb", false)]
    [InlineData("CREATEDB", "NOCREATEDB", "rolcreatedb", true)]
    [InlineData("CREATEROLE", "NOCREATEROLE", "rolcreaterole", false)]
    [InlineData("CREATEROLE", "NOCREATEROLE", "rolcreaterole", true)]
    [InlineData("REPLICATION", "NOREPLICATION", "rolreplication", false)]
    [InlineData("REPLICATION", "NOREPLICATION", "rolreplication", true)]
    public async Task Every_physical_connection_refuses_an_invalid_attribute_and_accepts_its_restoration(
        string altered, string restored, string predicate, bool synchronous)
    {
        await using var source = OutboxDispatcherDataSource.Build(postgres.OutboxConnectionString);
        // Occupy the first physical connection so the rejection exercises a new
        // connector in an already-used pool, not just the pool's first connection.
        await using var first = await source.OpenConnectionAsync();
        try
        {
            await postgres.ExecuteAsSuperuserAsync($"ALTER ROLE learnstack_outbox_admin {altered}");
            await using var raw = await PostgresFixture.OpenAsync(postgres.OutboxConnectionString);
            await using var truth = new NpgsqlCommand(
                $"SELECT {predicate} FROM pg_roles WHERE rolname = current_user", (NpgsqlConnection)raw);
            (await truth.ExecuteScalarAsync()).Should().Be(true, "the invalid server attribute must actually be present");

            await AssertGuardRefusesAsync(source, synchronous);
        }
        finally
        {
            await postgres.ExecuteAsSuperuserAsync($"ALTER ROLE learnstack_outbox_admin {restored}");
        }

        await using var healthy = await source.OpenConnectionAsync();
        healthy.State.Should().Be(System.Data.ConnectionState.Open);
        healthy.ProcessID.Should().NotBe(first.ProcessID);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Foreign_membership_is_refused_directly_and_through_a_bridge_then_restoration_connects(
        bool throughBridge, bool synchronous)
    {
        await using var source = OutboxDispatcherDataSource.Build(postgres.OutboxConnectionString);
        try
        {
            await postgres.ExecuteAsSuperuserAsync(throughBridge
                ? "CREATE ROLE outbox_guard_bridge NOLOGIN; "
                    + "GRANT learnstack_migration TO outbox_guard_bridge; "
                    + "GRANT outbox_guard_bridge TO learnstack_outbox_admin"
                : "GRANT learnstack_migration TO learnstack_outbox_admin");
            await using var raw = await PostgresFixture.OpenAsync(postgres.OutboxConnectionString);
            await using var truth = new NpgsqlCommand(
                "SELECT pg_has_role(current_user, 'learnstack_migration', 'MEMBER')", (NpgsqlConnection)raw);
            (await truth.ExecuteScalarAsync()).Should().Be(true, "the ownership escalation must be reachable");

            await AssertGuardRefusesAsync(source, synchronous);
        }
        finally
        {
            await postgres.ExecuteAsSuperuserAsync(throughBridge
                ? "DROP ROLE IF EXISTS outbox_guard_bridge"
                : "REVOKE learnstack_migration FROM learnstack_outbox_admin");
        }

        await using var healthy = await source.OpenConnectionAsync();
        healthy.State.Should().Be(System.Data.ConnectionState.Open);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task App_membership_in_the_dispatcher_is_refused_directly_and_transitively_then_restoration_connects(
        bool throughBridge, bool synchronous)
    {
        await using var source = OutboxDispatcherDataSource.Build(postgres.OutboxConnectionString);
        await using var first = await source.OpenConnectionAsync();
        await using var appSource = ApplicationDataSource.Build(postgres.AppConnectionString);
        // The app guard checks new physical connections. A later grant can still
        // escalate an existing session, so the dispatcher checks incoming grants too.
        await using var app = await appSource.OpenConnectionAsync();
        try
        {
            await postgres.ExecuteAsSuperuserAsync(throughBridge
                ? "CREATE ROLE outbox_guard_member_bridge NOLOGIN; "
                    + "GRANT learnstack_outbox_admin TO outbox_guard_member_bridge; "
                    + "GRANT outbox_guard_member_bridge TO learnstack_app"
                : "GRANT learnstack_outbox_admin TO learnstack_app");
            await using var switchRole = new NpgsqlCommand("SET ROLE learnstack_outbox_admin", app);
            await switchRole.ExecuteNonQueryAsync();
            await using var truth = new NpgsqlCommand(
                """
                SELECT session_user = 'learnstack_app'
                    AND current_user = 'learnstack_outbox_admin' AND rolbypassrls
                FROM pg_roles WHERE rolname = current_user
                """, app);
            (await truth.ExecuteScalarAsync()).Should().Be(true,
                "the direct or transitive grant must really let an existing app session acquire the bypass role");

            await AssertGuardRefusesAsync(source, synchronous);
        }
        finally
        {
            await postgres.ExecuteAsSuperuserAsync(throughBridge
                ? "DROP ROLE IF EXISTS outbox_guard_member_bridge"
                : "REVOKE learnstack_outbox_admin FROM learnstack_app");
            await using var resetRole = new NpgsqlCommand("RESET ROLE", app);
            await resetRole.ExecuteNonQueryAsync();
        }

        await using var healthy = await source.OpenConnectionAsync();
        healthy.State.Should().Be(System.Data.ConnectionState.Open);
        healthy.ProcessID.Should().NotBe(first.ProcessID);
    }

    [Fact]
    public async Task A_startup_role_switch_cannot_change_the_dispatcher_current_role()
    {
        try
        {
            await postgres.ExecuteAsSuperuserAsync(
                "CREATE ROLE outbox_guard_switched NOLOGIN; "
                + "GRANT outbox_guard_switched TO learnstack_outbox_admin");
            var switched = new NpgsqlConnectionStringBuilder(postgres.OutboxConnectionString)
            {
                Options = "-c role=outbox_guard_switched",
            }.ConnectionString;
            await using var raw = await PostgresFixture.OpenAsync(switched);
            await using var truth = new NpgsqlCommand("SELECT session_user, current_user", (NpgsqlConnection)raw);
            await using (var reader = await truth.ExecuteReaderAsync())
            {
                (await reader.ReadAsync()).Should().BeTrue();
                reader.GetString(0).Should().Be(OutboxDispatcherDataSource.Role);
                reader.GetString(1).Should().Be("outbox_guard_switched");
            }

            await using var source = OutboxDispatcherDataSource.Build(switched);
            await AssertGuardRefusesAsync(source, synchronous: false);
        }
        finally
        {
            await postgres.ExecuteAsSuperuserAsync("DROP ROLE IF EXISTS outbox_guard_switched");
        }

        await using var restored = OutboxDispatcherDataSource.Build(postgres.OutboxConnectionString);
        await using var healthy = await restored.OpenConnectionAsync();
        healthy.State.Should().Be(System.Data.ConnectionState.Open);
    }

    private static async Task AssertGuardRefusesAsync(NpgsqlDataSource source, bool synchronous)
    {
        InvalidOperationException failure;
        if (synchronous)
        {
            var open = () =>
            {
                using var connection = source.OpenConnection();
            };
            failure = open.Should().Throw<InvalidOperationException>().Which;
        }
        else
        {
            var open = async () =>
            {
                await using var connection = await source.OpenConnectionAsync();
            };
            failure = (await open.Should().ThrowAsync<InvalidOperationException>()).Which;
        }

        failure.Message.Should().Be("The outbox dispatcher connection has an invalid role, attributes or memberships.");
        failure.InnerException.Should().BeNull();
    }
}
