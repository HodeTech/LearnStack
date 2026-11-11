using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class ProviderEventStorageTests(IdentityMembershipFixture fixture) : IClassFixture<IdentityMembershipFixture>
{
    [Fact]
    public async Task Machine_role_has_only_fixed_function_and_minimized_ledger_access()
    {
        await using var machine = await MachineAsync();
        foreach (var table in new[] { "users", "memberships", "membership_profiles", "identity_provider_event_targets", "audit_log", "outbox_messages" })
        {
            await using var probe = new NpgsqlCommand("SELECT has_table_privilege(current_user,@table,'SELECT')", machine);
            probe.Parameters.AddWithValue("table", "public." + table);
            (await probe.ExecuteScalarAsync()).Should().Be(false);
        }
        await using var denied = new NpgsqlCommand("SELECT * FROM public.users", machine);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => denied.ExecuteNonQueryAsync());
        failure.SqlState.Should().Be(PostgresErrorCodes.InsufficientPrivilege);
        await using var app = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var privilege = new NpgsqlCommand("SELECT has_function_privilege(current_user,'public.fn_identity_provider_event_claim(uuid)','EXECUTE')", app);
        (await privilege.ExecuteScalarAsync()).Should().Be(false);
        await using var attributes = new NpgsqlCommand("""
            SELECT NOT rolinherit AND NOT rolbypassrls AND NOT rolsuper AND NOT rolcreaterole
                AND NOT rolcreatedb AND NOT rolreplication
            FROM pg_roles WHERE rolname=current_user
            """, machine);
        (await attributes.ExecuteScalarAsync()).Should().Be(true);
    }

    [Theory]
    [InlineData(false, "subject_absent")]
    [InlineData(true, "subject_unbound")]
    public async Task Nonbinding_facts_are_durable_receipt_only_without_allocating_a_User(bool withSubject, string disposition)
    {
        var id = Guid.CreateVersion7();
        var key = withSubject ? IdentitySubjectKey.Compute(IdentityMembershipFixture.Authority.AuthorityId, Guid.NewGuid().ToString("D")).ToByteArray() : null;
        var accepted = await AcceptAsync(id, "LOGIN_ERROR", key);
        accepted.Accepted.Should().BeTrue();
        accepted.Disposition.Should().Be(disposition);
        accepted.Count.Should().Be(0);
        (await AcceptAsync(id, "LOGIN_ERROR", key, accepted.At)).Should().Be(accepted);
    }

    [Fact]
    public async Task Duplicate_concurrent_acceptance_freezes_one_tenant_target_across_multiple_org_memberships()
    {
        var id = Guid.CreateVersion7();
        var at = DateTimeOffset.UtcNow.AddMinutes(-1);
        var key = SubjectKey(IdentityMembershipFixture.PersonA);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AcceptAsync(id, "LOGIN", key, at)));
        results.Should().OnlyContain(item => item.Accepted && item.Disposition == "routed" && item.Count == 1);
        results.Select(item => item.Id).Distinct().Should().ContainSingle();
        await using var app = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var tx = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, tx, SchemaFixture.TenantA);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, tx,
            "SELECT count(*) FROM public.identity_provider_event_targets WHERE provider_event_id=@id", ("id", id))).Should().Be(1);
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Changed_source_identity_is_unaccepted_and_cannot_rewrite_original_or_block_a_later_fact()
    {
        var id = Guid.CreateVersion7();
        var original = await AcceptAsync(id, "LOGIN", SubjectKey(IdentityMembershipFixture.PersonA));
        var conflict = await AcceptAsync(id, "LOGOUT", SubjectKey(IdentityMembershipFixture.PersonA), original.At);
        conflict.Accepted.Should().BeFalse();
        conflict.Disposition.Should().Be("source_collision");
        conflict.Id.Should().Be(original.Id);
        var later = await AcceptAsync(Guid.CreateVersion7(), "LOGIN_ERROR", SubjectKey(IdentityMembershipFixture.PersonB));
        later.Accepted.Should().BeTrue();
        (await AcceptAsync(id, "LOGIN", SubjectKey(IdentityMembershipFixture.PersonA), original.At)).Should().Be(original);
    }

    [Fact]
    public async Task Actual_app_RLS_hides_other_tenant_targets_and_unannounced_reads()
    {
        var id = Guid.CreateVersion7();
        await AcceptAsync(id, "LOGIN", SubjectKey(IdentityMembershipFixture.PersonA));
        await using var app = await OpenAsync(fixture.Postgres.AppConnectionString);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, null,
            "SELECT count(*) FROM public.identity_provider_event_targets WHERE provider_event_id=@id", ("id", id))).Should().Be(0);
        await using var tx = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, tx, SchemaFixture.TenantB);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, tx,
            "SELECT count(*) FROM public.identity_provider_event_targets WHERE provider_event_id=@id", ("id", id))).Should().Be(0);
        await IdentityMembershipFixture.ScopeAsync(app, tx, SchemaFixture.TenantA);
        (await IdentityMembershipFixture.ScalarAsync<long>(app, tx,
            "SELECT count(*) FROM public.identity_provider_event_targets WHERE provider_event_id=@id", ("id", id))).Should().Be(1);
        await using var model = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(app).Options,
            StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);
        await model.Database.UseTransactionAsync(tx);
        (await model.ProviderEventTargets.CountAsync(row => row.ProviderEventId == id)).Should().Be(0,
            "the independent EF filter refuses disclosure even when SQL's tenant was announced");
        await tx.RollbackAsync();
    }

    [Fact]
    public async Task Source_or_kind_substitution_refuses_before_acceptance()
    {
        var id = Guid.CreateVersion7();
        var rejected = await Assert.ThrowsAsync<PostgresException>(() => AcceptAsync(id, "CLIENT_LOGIN", null));
        rejected.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        foreach (var at in new[] { DateTimeOffset.UtcNow.AddDays(-31), DateTimeOffset.UtcNow.AddHours(1) })
        {
            var unaccepted = await AcceptAsync(Guid.CreateVersion7(), "LOGIN", null, at);
            unaccepted.Accepted.Should().BeFalse();
            unaccepted.Disposition.Should().Be("out_of_window");
        }
    }

    [Fact]
    public async Task Claimed_target_cannot_complete_without_its_outbox_on_the_same_commit()
    {
        await AcceptAsync(Guid.CreateVersion7(), "LOGOUT", SubjectKey(IdentityMembershipFixture.PersonA));
        await using var machine = await MachineAsync();
        await using var claim = new NpgsqlCommand("SELECT * FROM public.fn_identity_provider_event_claim(@token)", machine);
        claim.Parameters.AddWithValue("token", Guid.CreateVersion7());
        Guid target; Guid tenant;
        await using (var reader = await claim.ExecuteReaderAsync())
        {
            (await reader.ReadAsync()).Should().BeTrue();
            target = reader.GetGuid(0); tenant = reader.GetGuid(2);
        }
        await using var app = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var tx = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, tx, tenant);
        await IdentityMembershipFixture.ExecuteAsync(app, tx,
            "UPDATE public.identity_provider_event_targets SET enqueued_at=clock_timestamp() WHERE id=@id", ("id", target));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => tx.CommitAsync());
        refused.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    private static byte[] SubjectKey(Guid actor) =>
        IdentitySubjectKey.Compute(IdentityMembershipFixture.Authority.AuthorityId, "fixture-subject-" + actor.ToString("N")).ToByteArray();

    private async Task<NpgsqlConnection> MachineAsync()
    {
        var connection = await OpenAsync(fixture.Postgres.IdentityIngressConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(connection, null, """
            INSERT INTO public.identity_provider_event_scan(id,authority_id,realm_id,activation_lower,retention_seconds)
            VALUES(1,@authority,'learnstack',clock_timestamp()-interval '7 days',604800) ON CONFLICT(id) DO NOTHING
            """, ("authority", IdentityMembershipFixture.Authority.AuthorityId.Value));
        return connection;
    }

    private async Task<AcceptedFact> AcceptAsync(Guid id, string kind, byte[]? key, DateTimeOffset? when = null)
    {
        // Preserve timestamp precision across the duplicate controls and PostgreSQL round-trips.
        var input = when ?? DateTimeOffset.UtcNow.AddMinutes(-1);
        var at = new DateTimeOffset(input.Ticks / 10 * 10, TimeSpan.Zero);
        await using var machine = await MachineAsync();
        await using var command = new NpgsqlCommand("SELECT * FROM public.fn_identity_provider_event_accept(@authority,@realm,@event,@at,@kind,@key)", machine);
        command.Parameters.AddWithValue("authority", IdentityMembershipFixture.Authority.AuthorityId.Value);
        command.Parameters.AddWithValue("realm", "learnstack");
        command.Parameters.AddWithValue("event", id);
        command.Parameters.AddWithValue("at", at);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Bytea) { Value = (object?)key ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2), reader.GetInt32(3), at);
    }

    private static async Task<NpgsqlConnection> OpenAsync(string connectionString) =>
        (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);

    private sealed record AcceptedFact(Guid Id, string Disposition, bool Accepted, int Count, DateTimeOffset At);
}
