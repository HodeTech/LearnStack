using System.Data;
using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class InvitationStorageTests(IdentityMembershipFixture fixture) : IClassFixture<IdentityMembershipFixture>
{
    [Fact]
    public async Task Populated_app_role_and_independent_EF_filter_confine_both_tenants_and_branches()
    {
        await using var app = await OpenAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.invitations")).Should().Be(0);
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.invitations")).Should().Be(2);
        var accessor = new StaticTenantContextAccessor(new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null));
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(app).Options,
            accessor, EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(transaction);
        (await db.Invitations.CountAsync()).Should().Be(2);
        await ScopeAsync(app, transaction, SchemaFixture.TenantB);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.invitations")).Should().Be(1);
        (await db.Invitations.CountAsync()).Should().Be(0);
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.invitations")).Should().Be(3);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.invitations WHERE id=@id", ("id", InvitationSchemaSeed.BranchOfferA2))).Should().Be(0);
        await ExecuteAsync(app, transaction, "SELECT set_config('app.scope','tenant',true)");
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.invitations")).Should().Be(4);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        (await ScalarAsync<long>(app, transaction, """
            WITH changed AS (UPDATE public.invitations SET row_version=row_version+1,
                updated_at=clock_timestamp(),updated_by=@actor WHERE id=@id RETURNING 1) SELECT count(*) FROM changed
            """, ("actor", AdminA), ("id", InvitationSchemaSeed.BranchOfferA2))).Should().Be(0);
    }

    [Fact]
    public async Task Pending_slot_includes_expired_offers_and_revocation_releases_only_its_own_slot()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        const string email = "expired-slot@example.invalid";
        var historical = Guid.CreateVersion7();
        await InvitationSchemaSeed.InsertAsync(app, transaction, historical, SchemaFixture.TenantA,
            email: email, created: DateTimeOffset.UtcNow.AddDays(-20));
        (await ScalarAsync<bool>(app, transaction, "SELECT expires_at < clock_timestamp() FROM public.invitations WHERE id=@id", ("id", historical))).Should().BeTrue();
        await ExecuteAsync(app, transaction, "SAVEPOINT duplicate");
        var duplicate = () => InvitationSchemaSeed.InsertAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, email: email);
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23505");
        await ExecuteAsync(app, transaction, "ROLLBACK TO SAVEPOINT duplicate");
        await ExecuteAsync(app, transaction, """
            UPDATE public.invitations SET status='Revoked',verifier_digest=NULL,row_version=row_version+1,
                updated_at=clock_timestamp(),updated_by=@actor WHERE id=@id
            """, ("actor", AdminA), ("id", historical));
        await InvitationSchemaSeed.InsertAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA, email: email);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.invitations WHERE recipient_email=@email", ("email", email))).Should().Be(2);
    }

    [Theory]
    [InlineData("recipient_email='other@example.invalid'")]
    [InlineData("generation=generation+2")]
    [InlineData("verifier_digest=decode(repeat('01',32),'hex')")]
    [InlineData("expires_at=expires_at+interval '1 day'")]
    [InlineData("status='Accepted'")]
    [InlineData("status='Revoked'")]
    [InlineData("deleted_at=clock_timestamp(),deleted_by=created_by")]
    [InlineData("pinned_role_version=pinned_role_version+1")]
    public async Task Malformed_transition_refuses_with_fixed_storage_diagnostic(string assignment)
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        var mutate = () => MutateAsync(app, transaction, assignment);
        (await mutate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Missing_fence_and_cross_tenant_Role_cannot_create_an_offer()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await ExecuteAsync(app, transaction, "SAVEPOINT before_insert");
        var missing = () => InvitationSchemaSeed.InsertAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA);
        (await missing.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0301");
        await ExecuteAsync(app, transaction, "ROLLBACK TO SAVEPOINT before_insert");
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        var foreign = () => InvitationSchemaSeed.InsertAsync(app, transaction, Guid.CreateVersion7(), SchemaFixture.TenantA,
            roleId: RoleId(SchemaFixture.TenantB, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        (await foreign.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Theory]
    [InlineData("id")]
    [InlineData("generation_id")]
    [InlineData("current_delivery_job_id")]
    public async Task Non_RFC_UUIDs_cannot_pass_a_nullable_version_CHECK(string column)
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        var insert = () => InvitationSchemaSeed.InsertAsync(app, transaction,
            column == "id" ? Guid.Empty : Guid.CreateVersion7(), SchemaFixture.TenantA,
            generation: column == "generation_id" ? Guid.Empty : null,
            job: column == "current_delivery_job_id" ? Guid.Empty : null);
        var refused = (await insert.Should().ThrowAsync<PostgresException>()).Which;
        refused.SqlState.Should().Be("23514");
        refused.ConstraintName.Should().Be("ck_invitations_identifiers");
    }

    [Fact]
    public async Task Exact_function_requires_digest_host_and_active_subject_without_disclosing_mailbox()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        await ExecuteAsync(app, transaction, "SET TRANSACTION READ ONLY");
        var id = InvitationSchemaSeed.TenantOfferA;
        (await EntryCountAsync(app, transaction, id, SchemaFixture.TenantA, PersonA)).Should().Be(1);
        (await EntryCountAsync(app, transaction, id, SchemaFixture.TenantB, PersonA)).Should().Be(0);
        (await EntryCountAsync(app, transaction, id, SchemaFixture.TenantA, PersonA, new byte[32])).Should().Be(0);
        (await EntryCountAsync(app, transaction, id, SchemaFixture.TenantA, Guid.CreateVersion7())).Should().Be(0);
        (await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.invitations")).Should().Be(0);
        (await ScalarAsync<bool>(app, transaction, "SELECT has_function_privilege(current_user,'public.fn_identity_invitation_guard()','EXECUTE')")).Should().BeFalse();
    }

    [Fact]
    public async Task Accepted_result_retains_original_actor_and_membership_and_never_reopens()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        var keys = new[] { AdminA, PersonA }.Select(id => IdentityAdvisoryLockKey.ForUser(UserId.From(id))).Order().ToArray();
        foreach (var key in keys) await ExecuteAsync(app, transaction, "SELECT pg_advisory_xact_lock(@key)", ("key", key));
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        await MutateAsync(app, transaction, """
            status='Accepted',consumed_verifier_digest=verifier_digest,verifier_digest=NULL,
            accepted_by=@acceptor,result_membership_id=@member,acceptance_disposition='AlreadyMember',accepted_at=clock_timestamp()
            """, ("acceptor", PersonA), ("member", LearnerMembershipA));
        (await EntryCountAsync(app, transaction, InvitationSchemaSeed.TenantOfferA, SchemaFixture.TenantA, PersonA)).Should().Be(1);
        (await EntryCountAsync(app, transaction, InvitationSchemaSeed.TenantOfferA, SchemaFixture.TenantA, PersonB)).Should().Be(0);
        var reopen = () => MutateAsync(app, transaction, "status='Pending',accepted_by=NULL,result_membership_id=NULL,acceptance_disposition=NULL,accepted_at=NULL,verifier_digest=consumed_verifier_digest,consumed_verifier_digest=NULL");
        (await reopen.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    [Fact]
    public async Task Writable_eligibility_recheck_requires_both_real_User_fences()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        var observe = () => EntryCountAsync(app, transaction, InvitationSchemaSeed.TenantOfferA, SchemaFixture.TenantA, PersonA);
        (await observe.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0301");
    }

    [Fact]
    public async Task Receiptless_known_failure_is_terminal_and_cannot_be_rewritten_in_its_generation()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
        await MutateAsync(app, transaction, "submission_status='Failed'");
        (await ScalarAsync<bool>(app, transaction,
            "SELECT submission_status='Failed' AND submission_receipt_id IS NULL FROM public.invitations WHERE id=@id",
            ("id", InvitationSchemaSeed.TenantOfferA))).Should().BeTrue();
        var rewrite = () => MutateAsync(app, transaction, "submission_status='SubmissionAccepted',submission_receipt_id=@receipt", ("receipt", Guid.CreateVersion7()));
        (await rewrite.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("23514");
    }

    private async Task<NpgsqlConnection> OpenAsync() => (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);

    private static Task MutateAsync(NpgsqlConnection app, NpgsqlTransaction transaction, string assignment,
        params (string Name, object? Value)[] parameters) => ExecuteAsync(app, transaction,
        "UPDATE public.invitations SET " + assignment + ",row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@id",
        [.. parameters, ("actor", AdminA), ("id", InvitationSchemaSeed.TenantOfferA)]);

    private static async Task<long> EntryCountAsync(NpgsqlConnection app, NpgsqlTransaction transaction,
        Guid id, Guid tenant, Guid actor, byte[]? digest = null)
    {
        var subject = new GlobalIdentitySubject(Authority.AuthorityId, Authority.Issuer, "fixture-subject-" + actor.ToString("N"));
        await using var command = new NpgsqlCommand("SELECT count(*) FROM public.fn_identity_invitation_entry(@id,@digest,@tenant,@authority,@subject)", app, transaction);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("digest", NpgsqlDbType.Bytea, digest ?? InvitationSchemaSeed.Digest(id));
        command.Parameters.AddWithValue("tenant", tenant);
        command.Parameters.AddWithValue("authority", Authority.AuthorityId.Value);
        command.Parameters.AddWithValue("subject", NpgsqlDbType.Bytea, subject.SubjectKey.ToByteArray());
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection app, NpgsqlTransaction transaction, string sql,
        params (string Name, object? Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, app, transaction);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
