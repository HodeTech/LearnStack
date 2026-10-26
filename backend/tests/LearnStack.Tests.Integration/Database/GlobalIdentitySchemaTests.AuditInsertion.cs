using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentitySchemaTests
{
    [Theory]
    [InlineData("absent-scope")]
    [InlineData("source")]
    [InlineData("bootstrap-actor")]
    [InlineData("seed-actor")]
    [InlineData("extra-metadata")]
    [InlineData("authority")]
    [InlineData("subject")]
    [InlineData("target")]
    [InlineData("entity-type")]
    [InlineData("tenant")]
    [InlineData("organization")]
    public async Task Global_audit_INSERT_refuses_malformed_attribution_and_a_valid_app_twin_commits(string fault)
    {
        var subject = NewSubject();
        var clean = GlobalAuditCandidate.Create(subject);
        var dirty = clean with { Id = Guid.CreateVersion7() };
        var metadata = JsonSerializer.Deserialize<Dictionary<string, string>>(dirty.Metadata)!;
        switch (fault)
        {
            case "source": metadata["identity_source"] = "unregistered-source"; break;
            case "bootstrap-actor": dirty = dirty with { Actor = UserId.SystemActor.Value }; break;
            case "seed-actor": metadata["identity_source"] = "identity-seed-adoption"; break;
            case "extra-metadata": metadata["extra"] = "unregistered-value"; break;
            case "authority": metadata["identity_authority_id"] = Guid.NewGuid().ToString("D"); break;
            case "subject": metadata["identity_subject_key"] = new string('0', 64); break;
            case "target": dirty = dirty with { Target = Guid.NewGuid().ToString("D") }; break;
            case "entity-type": dirty = dirty with { EntityType = "Membership" }; break;
            case "tenant": dirty = dirty with { Tenant = Guid.CreateVersion7() }; break;
            case "organization": dirty = dirty with { Organization = Guid.CreateVersion7() }; break;
            case "absent-scope": break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        dirty = dirty with { Metadata = JsonSerializer.Serialize(metadata) };
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using (var transaction = await app.BeginTransactionAsync())
        {
            if (fault == "absent-scope") await ClearAsync(app, transaction);
            else await AnnounceAsync(app, transaction, subject);
            var insert = () => InsertGlobalAuditCandidateAsync(app, transaction, dirty);
            (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514",
                "the immediate attribution guard rejects the malformed row before deferred evidence");
            await transaction.RollbackAsync();
        }
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await AnnounceAsync(app, transaction, subject);
            await InsertGlobalAuditCandidateAsync(app, transaction, clean);
            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.CommitAsync();
        }
        await AssertGlobalAuditCountAsync(fixture.Postgres.PlatformConnectionString, dirty.Id, 0);
        await AssertGlobalAuditCountAsync(fixture.Postgres.PlatformConnectionString, clean.Id, 1);
    }

    [Theory]
    [InlineData("migration")]
    [InlineData("platform")]
    public async Task Global_creation_audit_cannot_be_inserted_through_owner_or_platform_credentials(string role)
    {
        var subject = NewSubject();
        var candidate = GlobalAuditCandidate.Create(subject);
        var connectionString = role == "migration" ? fixture.Postgres.MigrationConnectionString : fixture.Postgres.PlatformConnectionString;
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await AnnounceAsync(connection, transaction, subject);
        var insert = () => InsertGlobalAuditCandidateAsync(connection, transaction, candidate);
        (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
        await transaction.RollbackAsync();
        await AssertGlobalAuditCountAsync(fixture.Postgres.PlatformConnectionString, candidate.Id, 0);
    }

    [Fact]
    public async Task Global_audit_WITH_CHECK_independently_refuses_missing_subject_and_detects_a_widened_policy()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        var subject = NewSubject();
        var candidate = GlobalAuditCandidate.Create(subject);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        // Both twins remove the earlier/late guards so the policy alone must
        // reject a valid-shaped row with no announced global authority.
        await ExecuteAsync(owner, null, """
            ALTER TABLE public.audit_log DISABLE TRIGGER tg_audit_log_identity_insert;
            ALTER TABLE public.audit_log DISABLE TRIGGER tg_audit_log_identity_creation;
            """);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using (var clean = await app.BeginTransactionAsync())
        {
            await ClearAsync(app, clean);
            var insert = () => InsertGlobalAuditCandidateAsync(app, clean, candidate);
            (await insert.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("42501");
            await clean.RollbackAsync();
        }
        await AssertGlobalAuditCountAsync(database.PlatformConnectionString, candidate.Id, 0);
        await ExecuteAsync(owner, null, "ALTER POLICY audit_log_isolation ON public.audit_log WITH CHECK (true)");
        await using (var dirty = await app.BeginTransactionAsync())
        {
            await ClearAsync(app, dirty);
            await InsertGlobalAuditCandidateAsync(app, dirty, candidate);
            await dirty.CommitAsync();
        }
        await AssertGlobalAuditCountAsync(database.PlatformConnectionString, candidate.Id, 1);
    }

    private static async Task InsertGlobalAuditCandidateAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, GlobalAuditCandidate candidate)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO public.audit_log(id,timestamp,tenant_id,organization_id,actor_user_id,module,operation,
                operation_type,operation_class,entity_type,entity_id,outcome,metadata)
            VALUES(@id,@at,@tenant,@organization,@actor,'identity','identity.user.create',
                'SecurityEvent','Must',@entity_type,@target,'failed',@metadata::jsonb)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", candidate.Id);
        command.Parameters.AddWithValue("at", candidate.At);
        command.Parameters.AddWithValue("tenant", candidate.Tenant);
        command.Parameters.AddWithValue("organization", NpgsqlDbType.Uuid, (object?)candidate.Organization ?? DBNull.Value);
        command.Parameters.AddWithValue("actor", NpgsqlDbType.Uuid, (object?)candidate.Actor ?? DBNull.Value);
        command.Parameters.AddWithValue("entity_type", candidate.EntityType);
        command.Parameters.AddWithValue("target", candidate.Target);
        command.Parameters.AddWithValue("metadata", candidate.Metadata);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task AssertGlobalAuditCountAsync(string connectionString, Guid id, long expected)
    {
        await using var reader = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM public.audit_log WHERE id=@id", reader);
        command.Parameters.AddWithValue("id", id);
        ((long)(await command.ExecuteScalarAsync())!).Should().Be(expected);
    }

    private sealed record GlobalAuditCandidate(Guid Id, DateTimeOffset At, Guid Tenant, Guid? Organization,
        Guid? Actor, string EntityType, string Target, string Metadata)
    {
        public static GlobalAuditCandidate Create(GlobalIdentitySubject subject) => new(Guid.CreateVersion7(),
            GlobalIdentityCreationReference.NormalizeTimestamp(DateTimeOffset.UtcNow), TenantId.PlatformSentinel.Value,
            null, null, "User", Guid.CreateVersion7().ToString("D"), JsonSerializer.Serialize(new
            {
                identity_authority_id = subject.AuthorityId.Value.ToString("D"),
                identity_subject_key = subject.SubjectKey.Hex,
                identity_source = "identity-bootstrap",
            }));
    }
}
