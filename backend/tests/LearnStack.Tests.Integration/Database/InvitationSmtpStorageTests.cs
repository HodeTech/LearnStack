using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class InvitationSmtpStorageTests(InvitationSmtpStorageFixture fixture) : IClassFixture<InvitationSmtpStorageFixture>
{
    [Fact]
    public async Task App_has_only_immutable_envelope_insert_and_two_function_grants()
    {
        await using var app = await OpenAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        foreach (var table in SchemaTableClasses.InvitationProvider)
            foreach (var action in new[] { "SELECT", "UPDATE", "DELETE", "TRUNCATE" })
                (await ScalarAsync<bool>(app, $"SELECT has_table_privilege(current_user,'public.{table}','{action}')")).Should().BeFalse();
        (await ScalarAsync<bool>(app, "SELECT has_any_column_privilege(current_user,'public.invitation_email_envelopes','INSERT')")).Should().BeTrue();
        (await ScalarAsync<bool>(app, "SELECT has_any_column_privilege(current_user,'public.invitation_email_attempt_receipts','INSERT')")).Should().BeFalse();
        (await ScalarAsync<long>(app, """
            SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace
            WHERE n.nspname='public' AND p.prosecdef
            """)).Should().Be(11);
    }

    [Fact]
    public async Task Claim_returns_ciphertext_once_and_durable_receipt_replays_without_message()
    {
        var facts = await SeedAsync();
        var claim = Guid.CreateVersion7();
        var first = await ClaimAsync(facts, claim);
        first.State.Should().Be("sending");
        first.HasCiphertext.Should().BeTrue();
        var duplicate = await ClaimAsync(facts, Guid.CreateVersion7());
        duplicate.State.Should().Be("sending");
        duplicate.HasCiphertext.Should().BeFalse();
        var receipt = Guid.CreateVersion7();
        var evidence = Guid.CreateVersion7();
        (await CompleteAsync(facts, claim, "accepted", receipt, evidence)).Should().Be(receipt);
        (await CompleteAsync(facts, claim, "accepted", receipt, evidence)).Should().Be(receipt);
        var replay = await ClaimAsync(facts, Guid.CreateVersion7());
        replay.State.Should().Be("accepted");
        replay.Receipt.Should().Be(receipt);
        replay.HasCiphertext.Should().BeFalse();
        var conflict = () => CompleteAsync(facts, claim, "permanent_rejected", Guid.CreateVersion7(), Guid.CreateVersion7());
        (await conflict.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0393");
    }

    [Fact]
    public async Task Forged_scope_claim_and_null_claim_cannot_select_terminal_evidence()
    {
        var facts = await SeedAsync();
        var wrong = () => ClaimAsync(facts with { Tenant = SchemaFixture.TenantB }, Guid.CreateVersion7());
        (await wrong.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0393");
        var claim = Guid.CreateVersion7();
        await ClaimAsync(facts, claim);
        var forged = () => CompleteAsync(facts, Guid.CreateVersion7(), "accepted", Guid.CreateVersion7(), Guid.CreateVersion7());
        (await forged.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0393");
        var missing = () => CompleteAsync(facts, null, "accepted", Guid.CreateVersion7(), Guid.CreateVersion7());
        (await missing.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0393");
        (await ClaimAsync(facts, Guid.CreateVersion7())).State.Should().Be("sending");
    }

    [Theory]
    [InlineData("until", null)]
    [InlineData("wake", null)]
    [InlineData("admission", null)]
    [InlineData("deadline", null)]
    [InlineData("fingerprint", null)]
    [InlineData("claim", null)]
    [InlineData("invitation", "00000000-0000-0000-0000-000000000001")]
    [InlineData("claim", "00000000-0000-0000-0000-000000000001")]
    [InlineData("recovery", "00000000-0000-0000-0000-000000000000")]
    public async Task Claim_refuses_missing_or_noncanonical_authority(string parameter, string? value)
    {
        var facts = await SeedAsync();
        await using var app = await OpenAsync();
        await using var command = Binding(app, facts, "SELECT * FROM public.fn_invitation_smtp_claim_or_read(@tenant,NULL,@spool,@invitation,@generation,@job,@attempt,@operation,@fingerprint,@admission,@deadline,@recovery,@wake,@until,@claim,@receipt,@evidence,false)");
        command.Parameters.AddWithValue("claim", Guid.CreateVersion7());
        command.Parameters.AddWithValue("receipt", Guid.CreateVersion7());
        command.Parameters.AddWithValue("evidence", Guid.CreateVersion7());
        command.Parameters.AddWithValue("recovery", NpgsqlDbType.Uuid, DBNull.Value);
        command.Parameters.AddWithValue("wake", 1L);
        command.Parameters.AddWithValue("until", facts.Deadline);
        // Preserve inferred parameter type before replacing its value with SQL NULL.
        var type = command.Parameters[parameter].NpgsqlDbType;
        command.Parameters[parameter].NpgsqlDbType = type;
        command.Parameters[parameter].Value = value is null ? DBNull.Value : Guid.Parse(value);
        var refused = () => command.ExecuteNonQueryAsync();
        (await refused.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0393");
    }

    private async Task<Facts> SeedAsync()
    {
        var invitation = Guid.CreateVersion7(); var generation = Guid.CreateVersion7(); var spool = Guid.CreateVersion7();
        var parameters = new InvitationEmailParameters(invitation, generation, spool);
        var json = BackgroundJobPayloadContract.Serialize(parameters, typeof(InvitationEmailParameters), BackgroundJobPayloadKind.Parameters);
        var job = await BackgroundJobRuntimeRows.InsertAsync(fixture.Postgres.AppConnectionString,
            "identity.invitation_email_tenant.v1", parameters: json, capacityGroup: "identity.invitation_email", profile: "external");
        var token = Guid.NewGuid(); var attempt = Guid.CreateVersion7(); var operation = Guid.NewGuid();
        var deadline = BackgroundJobSchemaSeed.Microseconds(DateTimeOffset.UtcNow.AddMinutes(3));
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        await using (var app = await OpenAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO public.invitation_email_envelopes(id,tenant_id,invitation_id,generation_id,job_id,format_version,key_version,nonce,ciphertext,tag)
                VALUES(@spool,@tenant,@invitation,@generation,@job,1,'k1',decode(repeat('01',12),'hex'),decode('012345','hex'),decode(repeat('02',16),'hex'));
                INSERT INTO public.background_job_capacity(tenant_id,capacity_group) VALUES(@tenant,'identity.invitation_email') ON CONFLICT DO NOTHING;
                """, ("spool", spool), ("tenant", SchemaFixture.TenantA), ("invitation", invitation), ("generation", generation), ("job", job.Value));
            await transaction.CommitAsync();
        }
        await BackgroundJobSchemaSeed.ClaimAsync(fixture.Postgres.OutboxConnectionString, job.Value, token);
        await using (var app = await OpenAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
            await BackgroundJobSchemaSeed.AdmitAsync(app, transaction, job.Value, attempt, token, SchemaFixture.TenantA, null, "external", deadline);
            await SchemaQueries.ExecuteAsync(app, transaction, """
                INSERT INTO public.background_job_reservations(tenant_id,capacity_group,job_id,attempt_id) VALUES(@tenant,'identity.invitation_email',@job,@attempt);
                UPDATE public.background_jobs SET phase='prepared',plan=@plan,plan_fingerprint=@fingerprint,operation_id=@operation,prepared_at=clock_timestamp() WHERE id=@job;
                UPDATE public.background_job_attempts SET plan_fingerprint=@fingerprint,operation_id=@operation,execute_intent_at=clock_timestamp() WHERE id=@attempt;
                UPDATE public.background_jobs SET phase='executing' WHERE id=@job;
                """, ("tenant", SchemaFixture.TenantA), ("job", job.Value), ("attempt", attempt), ("plan", json), ("fingerprint", fingerprint), ("operation", operation));
            await transaction.CommitAsync();
        }
        return new(SchemaFixture.TenantA, spool, invitation, generation, job.Value, attempt, operation, fingerprint, token, deadline);
    }
    private async Task<(string State, Guid? Receipt, bool HasCiphertext)> ClaimAsync(Facts facts, Guid claim)
    {
        await using var app = await OpenAsync();
        await using var command = Binding(app, facts, "SELECT * FROM public.fn_invitation_smtp_claim_or_read(@tenant,NULL,@spool,@invitation,@generation,@job,@attempt,@operation,@fingerprint,@admission,@deadline,NULL,1,@deadline,@claim,@receipt,@evidence,false)");
        command.Parameters.AddWithValue("claim", claim);
        command.Parameters.AddWithValue("receipt", Guid.CreateVersion7());
        command.Parameters.AddWithValue("evidence", Guid.CreateVersion7());
        await using var row = await command.ExecuteReaderAsync();
        (await row.ReadAsync()).Should().BeTrue();
        return (row.GetString(0), row.IsDBNull(2) ? null : row.GetGuid(2), !row.IsDBNull(4));
    }
    private async Task<Guid> CompleteAsync(Facts facts, Guid? claim, string state, Guid receipt, Guid evidence)
    {
        await using var app = await OpenAsync();
        await using var command = Binding(app, facts, "SELECT receipt_id FROM public.fn_invitation_smtp_complete_attempt(@tenant,NULL,@spool,@invitation,@generation,@job,@attempt,@operation,@fingerprint,@admission,@deadline,@claim,@state,@receipt,@evidence,false)");
        command.Parameters.AddWithValue("claim", NpgsqlDbType.Uuid, (object?)claim ?? DBNull.Value);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("receipt", receipt);
        command.Parameters.AddWithValue("evidence", evidence);
        return (Guid)(await command.ExecuteScalarAsync())!;
    }
    private static NpgsqlCommand Binding(NpgsqlConnection app, Facts facts, string sql)
    {
        var command = new NpgsqlCommand(sql, app);
        command.Parameters.AddWithValue("tenant", facts.Tenant); command.Parameters.AddWithValue("spool", facts.Spool);
        command.Parameters.AddWithValue("invitation", facts.Invitation); command.Parameters.AddWithValue("generation", facts.Generation);
        command.Parameters.AddWithValue("job", facts.Job); command.Parameters.AddWithValue("attempt", facts.Attempt);
        command.Parameters.AddWithValue("operation", facts.Operation); command.Parameters.AddWithValue("fingerprint", facts.Fingerprint);
        command.Parameters.AddWithValue("admission", facts.Admission); command.Parameters.AddWithValue("deadline", facts.Deadline);
        return command;
    }
    private Task<NpgsqlConnection> OpenAsync() => OpenCoreAsync(fixture.Postgres.AppConnectionString);
    private static async Task<NpgsqlConnection> OpenCoreAsync(string connectionString) => (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
    private sealed record Facts(Guid Tenant, Guid Spool, Guid Invitation, Guid Generation, Guid Job, Guid Attempt, Guid Operation, string Fingerprint, Guid Admission, DateTimeOffset Deadline);
}
public sealed class InvitationSmtpStorageFixture : IAsyncLifetime
{
    public PostgresFixture Postgres { get; } = new();
    public async Task InitializeAsync()
    {
        await Postgres.InitializeAsync();
        await MigrationChains.ApplyAllAsync(Postgres.MigrationConnectionString);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(Postgres.AppConnectionString);
    }
    public Task DisposeAsync() => Postgres.DisposeAsync();
}
