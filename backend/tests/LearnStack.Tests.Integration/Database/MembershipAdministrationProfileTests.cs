using System.Text.Json;
using System.Data.Common;
using System.Diagnostics;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MembershipAdministrationProfileTests(MembershipAdministrationFixture fixture)
    : IClassFixture<MembershipAdministrationFixture>
{
    [Fact]
    public async Task Own_reader_requires_a_clean_announced_owner_and_works_on_a_physically_READ_ONLY_transaction()
    {
        await using var harness = await fixture.CreateAsync("membership-own-physical-readonly", PersonA, SchemaFixture.OrgA1);
        Assert.Throws<InvalidOperationException>(() => harness.Services.GetRequiredService<IMembershipAdministrationReader>());
        Assert.False(harness.Unit.HasActiveTransaction);
        await using var frame = await harness.Unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
        var reader = harness.Services.GetRequiredService<IMembershipAdministrationReader>();
        var db = harness.Services.GetRequiredService<IdentityDbContext>();
        var own = await reader.ReadOwnAsync();
        Assert.NotNull(own);
        Assert.Equal(BranchMembershipA1, own.Id.Value);
        Assert.Empty(db.ChangeTracker.Entries());
        Assert.Equal("on", await ScalarAsync<string>((NpgsqlConnection)harness.Unit.Connection,
            (NpgsqlTransaction)harness.Unit.Transaction!, "SHOW transaction_read_only"));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => reader.ReadDetailAsync(MembershipId.From(BranchMembershipA1)));
        db.Memberships.Attach(own);
        Assert.True(own.SetProfile("dirty-own-canary", null, DateTimeOffset.UtcNow, UserId.From(PersonA)).IsSuccess);
        await Assert.ThrowsAsync<TenantContextMissingException>(() => reader.ReadOwnAsync());
        await frame.FailAsync();
    }

    [Fact]
    public async Task Direct_handler_invocation_cannot_lookup_or_mutate_without_the_current_application_gate()
    {
        await using var harness = await fixture.CreateAsync("membership-direct-handler");
        Assert.Throws<InvalidOperationException>(() => harness.Services.GetRequiredService<
            IRequestHandler<SetMembershipProfileCommand, Result<MembershipMutationResult>>>());
        Assert.False(harness.Unit.HasActiveTransaction);
        await using var deployment = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        await ExecuteAsync(deployment, null, """
            CREATE FUNCTION member_direct_lookup_proof() RETURNS boolean LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'direct handler performed a Member lookup' USING ERRCODE='P0399'; END $$;
            REVOKE ALL ON FUNCTION member_direct_lookup_proof() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION member_direct_lookup_proof() TO learnstack_app;
            CREATE POLICY member_direct_lookup_proof ON memberships AS RESTRICTIVE FOR SELECT TO learnstack_app
            USING(member_direct_lookup_proof());
            """);
        try
        {
            await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString))
            {
                await SchemaQueries.AssertApplicationRoleAsync(app);
                await using var transaction = await app.BeginTransactionAsync();
                await ScopeAsync(app, transaction, SchemaFixture.TenantA);
                var lookup = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<Guid>(app, transaction,
                    "SELECT id FROM memberships WHERE id=@member", ("member", LearnerMembershipA)));
                Assert.Equal("P0399", lookup.SqlState);
                Assert.Equal("direct handler performed a Member lookup", lookup.MessageText);
                await transaction.RollbackAsync();
            }
            await using var frame = await harness.Unit.BeginTransactionAsync(TransactionMode.ReadWrite);
            await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
            var request = new SetMembershipProfileCommand(LearnerMembershipA, 0, "direct-canary", "en");
            var handler = harness.Services.GetRequiredService<IRequestHandler<SetMembershipProfileCommand, Result<MembershipMutationResult>>>();
            Assert.Equal("forbidden", (await handler.Handle(request, CancellationToken.None)).Error!.Code);
            await frame.FailAsync();
        }
        finally
        {
            await ExecuteAsync(deployment, null, "DROP POLICY member_direct_lookup_proof ON memberships; DROP FUNCTION member_direct_lookup_proof()");
        }
        Assert.False(harness.Unit.HasActiveTransaction);
        Assert.Equal(0, (await fixture.ReadAsync(LearnerMembershipA)).Version);
    }

    [Fact]
    public async Task Own_profile_reads_the_exact_branch_and_commits_no_navigation_audit()
    {
        var person = await fixture.AddMemberAsync(SchemaFixture.OrgA1);
        await fixture.AddMembershipAsync(person.Actor, SchemaFixture.OrgA2);
        await fixture.MutateAsync(SchemaFixture.TenantA, SchemaFixture.OrgA1, null, false, (app, transaction) =>
            ExecuteAsync(app, transaction, "UPDATE membership_profiles SET display_name='branch-one-only' WHERE membership_id=@id", ("id", person.Membership)));
        var result = await fixture.SendAsync(new GetOwnMembershipProfileQuery(), "membership-own-exact", person.Actor, SchemaFixture.OrgA1);
        Assert.True(result.IsSuccess);
        Assert.Equal("branch-one-only", result.Value.DisplayName);
        Assert.Equal("en", result.Value.PreferredLocale);
        Assert.Equal(0, result.Value.Version);
        Assert.Empty(await fixture.AuditAsync("membership-own-exact", "identity.membership_profile.read_own", SchemaFixture.OrgA1));
        var other = await fixture.SendAsync(new GetOwnMembershipProfileQuery(), "membership-own-other", person.Actor, SchemaFixture.OrgA2);
        Assert.True(other.IsSuccess);
        Assert.Equal("profile-canary", other.Value.DisplayName);
    }

    [Fact]
    public async Task Own_profile_missing_in_a_branch_never_falls_back_to_the_NULL_organization_membership()
    {
        var result = await fixture.SendAsync(new GetOwnMembershipProfileQuery(), "membership-own-no-fallback", AdminA, SchemaFixture.OrgA1);
        Assert.Equal("not_found", result.Error!.Code);
        Assert.Equal(0, (await fixture.ReadAsync(AdminMembershipA)).Version);
    }

    [Fact]
    public async Task Own_write_needs_profile_authority_and_changes_only_the_exact_actor_branch()
    {
        var person = await fixture.AddMemberAsync(SchemaFixture.OrgA2);
        var orgless = await fixture.AddMembershipAsync(person.Actor, null);
        var result = await fixture.SendAsync(new SetOwnMembershipProfileCommand(0, "own-write-canary", "tr"),
            "membership-own-write", person.Actor, SchemaFixture.OrgA2);
        Assert.True(result.IsSuccess);
        Assert.Equal(person.Membership, result.Value.MembershipId);
        Assert.Equal(1, result.Value.Version);
        Assert.Equal("own-write-canary", (await fixture.ReadAsync(person.Membership, SchemaFixture.OrgA2)).DisplayName);
        Assert.Equal("profile-canary", (await fixture.ReadAsync(orgless)).DisplayName);
        var row = Assert.Single(await fixture.AuditAsync("membership-own-write", "identity.membership_profile.write", SchemaFixture.OrgA2));
        Assert.DoesNotContain("own-write-canary", row, StringComparison.Ordinal);
        Assert.DoesNotContain("\"tr\"", row, StringComparison.Ordinal);
        using var audit = JsonDocument.Parse(row);
        Assert.Equal("Must", audit.RootElement.GetProperty("class").GetString());
        Assert.Equal("Update", audit.RootElement.GetProperty("type").GetString());
        Assert.Equal(person.Membership.ToString("D"), audit.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Another_person_profile_requires_both_applicable_permissions_while_own_write_needs_only_profile_write()
    {
        var actor = await fixture.AddMemberAsync(SchemaFixture.OrgA1, BuiltInRoleKey.OrgInstructor);
        var target = await fixture.AddMemberAsync(SchemaFixture.OrgA1);
        try
        {
            await fixture.SetRoleGrantsAsync(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization,
                new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite));
            var other = await fixture.SendAsync(new SetMembershipProfileCommand(target.Membership, 0, "another-canary", null),
                "membership-profile-only-other", actor.Actor, SchemaFixture.OrgA1);
            Assert.Equal("forbidden", other.Error!.Code);
            Assert.True((await fixture.SendAsync(new SetOwnMembershipProfileCommand(0, "profile-only-own", null),
                "membership-profile-only-own", actor.Actor, SchemaFixture.OrgA1)).IsSuccess);
            await fixture.SetRoleGrantsAsync(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization,
                new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.MembershipAdmin));
            Assert.Equal("forbidden", (await fixture.SendAsync(new SetMembershipProfileCommand(target.Membership, 0, null, null),
                "membership-admin-only-other", actor.Actor, SchemaFixture.OrgA1)).Error!.Code);
            Assert.Equal(0, (await fixture.ReadAsync(target.Membership, SchemaFixture.OrgA1)).Version);
        }
        finally { await fixture.SetRoleGrantsAsync(BuiltInRoleKey.OrgInstructor, PermissionScope.Organization); }
    }

    [Fact]
    public async Task Tenant_detail_witness_reads_another_branch_but_Organization_authority_stays_exactly_scoped()
    {
        var branchAdmin = await fixture.AddMemberAsync(SchemaFixture.OrgA1, BuiltInRoleKey.OrgAdmin);
        var tenant = await fixture.SendAsync(new GetMembershipDetailQuery(BranchMembershipA2), "membership-detail-tenant", AdminA, SchemaFixture.OrgA1);
        Assert.True(tenant.IsSuccess);
        Assert.Equal(OrganizationId.From(SchemaFixture.OrgA2), tenant.Value.OrganizationId);
        Assert.Single(tenant.Value.RoleIds);
        Assert.Equal("not_found", (await fixture.SendAsync(new GetMembershipDetailQuery(BranchMembershipA2),
            "membership-detail-other-branch", branchAdmin.Actor, SchemaFixture.OrgA1)).Error!.Code);
        Assert.True((await fixture.SendAsync(new GetMembershipDetailQuery(BranchMembershipA1),
            "membership-detail-own-branch", branchAdmin.Actor, SchemaFixture.OrgA1)).IsSuccess);
        Assert.Equal("not_found", (await fixture.SendAsync(new GetMembershipDetailQuery(BranchMembershipB),
            "membership-detail-other-tenant")).Error!.Code);
        Assert.Single(await fixture.AuditAsync("membership-detail-tenant", "identity.membership.read", SchemaFixture.OrgA1));
    }

    [Fact]
    public async Task Writes_never_inherit_the_Tenant_detail_read_hatch()
    {
        var result = await fixture.SendAsync(new SetMembershipProfileCommand(BranchMembershipA2, 0, "wrong-branch", null),
            "membership-write-no-hatch", AdminA, SchemaFixture.OrgA1);
        Assert.Equal("not_found", result.Error!.Code);
        var orgless = await fixture.SendAsync(new SetMembershipProfileCommand(LearnerMembershipA, 0, "wrong-null", null),
            "membership-write-no-null-hatch", AdminA, SchemaFixture.OrgA1);
        Assert.Equal("not_found", orgless.Error!.Code);
        Assert.Equal(0, (await fixture.ReadAsync(LearnerMembershipA)).Version);
    }

    [Fact]
    public async Task Profile_versions_noop_and_enabled_locale_are_checked_before_stamping()
    {
        var member = await fixture.AddMemberAsync();
        Assert.Equal("concurrency_conflict", (await fixture.SendAsync(new SetMembershipProfileCommand(member.Membership, 99, null, null),
            "membership-profile-stale")).Error!.Code);
        var before = await fixture.ReadAsync(member.Membership);
        var same = await fixture.SendAsync(new SetMembershipProfileCommand(member.Membership, 0, before.DisplayName, before.PreferredLocale),
            "membership-profile-noop");
        Assert.True(same.IsSuccess);
        Assert.Equal(0, same.Value.Version);
        Assert.Equal(before.RootStamp, (await fixture.ReadAsync(member.Membership)).RootStamp);
        Assert.Equal("validation_failed", (await fixture.SendAsync(new SetMembershipProfileCommand(member.Membership, 0, "unsupported-locale", "de"),
            "membership-profile-locale-refused")).Error!.Code);
        Assert.Equal(0, (await fixture.ReadAsync(member.Membership)).Version);
        Assert.True((await fixture.SendAsync(new SetMembershipProfileCommand(member.Membership, 0, null, null),
            "membership-profile-clear")).IsSuccess);
        Assert.Null((await fixture.ReadAsync(member.Membership)).DisplayName);
    }

    [Fact]
    public async Task Actual_MUST_detail_audit_failure_prevents_the_contextual_DTO_from_being_disclosed()
    {
        var member = await fixture.AddMemberAsync();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION membership_detail_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.correlation_id='membership-detail-audit-refusal' AND NEW.operation='identity.membership.read'
                    AND NEW.outcome='success' THEN
                    RAISE EXCEPTION 'fixed membership detail audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_membership_detail_audit_proof BEFORE INSERT ON audit_log
            FOR EACH ROW EXECUTE FUNCTION membership_detail_audit_proof();
            """);
        try
        {
            await using var harness = await fixture.CreateAsync("membership-detail-audit-refusal");
            Result<MembershipDetailDto>? published = null;
            var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(async () => published = await harness.Sender.Send(
                new GetMembershipDetailQuery(member.Membership)));
            Assert.Equal("audit_unavailable", failure.Error.Code);
            Assert.Null(published);
            Assert.False(harness.Unit.HasActiveTransaction);
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP TRIGGER tg_membership_detail_audit_proof ON audit_log; DROP FUNCTION membership_detail_audit_proof()");
        }
        var disclosed = await fixture.SendAsync(new GetMembershipDetailQuery(member.Membership), "membership-detail-audit-clean");
        Assert.True(disclosed.IsSuccess);
        Assert.Equal(member.Membership, disclosed.Value.MembershipId);
        Assert.Equal("profile-canary", disclosed.Value.DisplayName);
        var row = Assert.Single(await fixture.AuditAsync("membership-detail-audit-clean", "identity.membership.read"));
        using var audit = JsonDocument.Parse(row);
        Assert.Equal("success", audit.RootElement.GetProperty("outcome").GetString());
        Assert.Equal("Must", audit.RootElement.GetProperty("class").GetString());
        Assert.Equal("ReadSensitive", audit.RootElement.GetProperty("type").GetString());
        Assert.Equal(member.Membership.ToString("D"), audit.RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Actual_MUST_profile_audit_failure_rolls_back_root_and_contained_PII_before_any_result()
    {
        var member = await fixture.AddMemberAsync();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION membership_profile_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF NEW.correlation_id='membership-profile-audit-refusal' AND NEW.operation='identity.membership_profile.write' THEN
                    RAISE EXCEPTION 'fixed membership audit refusal' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER tg_membership_profile_audit_proof BEFORE INSERT ON audit_log
            FOR EACH ROW EXECUTE FUNCTION membership_profile_audit_proof();
            """);
        try
        {
            await using var harness = await fixture.CreateAsync("membership-profile-audit-refusal");
            Result<MembershipMutationResult>? published = null;
            await Assert.ThrowsAsync<AuditWriteFailedException>(async () => published = await harness.Sender.Send(
                new SetMembershipProfileCommand(member.Membership, 0, "atomic-profile-canary", "tr")));
            Assert.Null(published);
            var after = await fixture.ReadAsync(member.Membership);
            Assert.Equal(0, after.Version);
            Assert.Equal("profile-canary", after.DisplayName);
            Assert.Equal("en", after.PreferredLocale);
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP TRIGGER tg_membership_profile_audit_proof ON audit_log; DROP FUNCTION membership_profile_audit_proof()");
        }
    }

}

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(IdentityDiagnosticSinkGroup.Name)]
public sealed class MembershipAdministrationDiagnosticTests(MembershipAdministrationFixture fixture)
    : IClassFixture<MembershipAdministrationFixture>
{
    [Fact]
    public async Task Stored_neutral_nested_and_sensitive_profile_values_never_escape_actual_read_failure_diagnostic_sinks()
    {
        var person = await fixture.AddMemberAsync();
        var canaries = new[] { "neutral-827be", "nested-912ac", "secret-613fd" };
        var payload = JsonSerializer.Serialize(new { value = canaries[0], items = new[] { new { value = canaries[1], token = canaries[2] } } });
        await fixture.MutateAsync(SchemaFixture.TenantA, null, null, false, (app, transaction) =>
            ExecuteAsync(app, transaction, "UPDATE membership_profiles SET display_name=@payload WHERE membership_id=@id", ("payload", payload), ("id", person.Membership)));
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        var policyBefore = await ScalarAsync<string>(owner, null, """
            SELECT jsonb_agg(jsonb_build_array(polname,pg_get_expr(polqual,polrelid),pg_get_expr(polwithcheck,polrelid)) ORDER BY polname)::text
            FROM pg_policy WHERE polrelid='public.memberships'::regclass
            """);
        using var sinks = new IdentityDiagnosticSinks();
        using var callers = new ActivitySource("learnstack.identity.storage-qualification");
        using var caller = callers.StartActivity("membership-administration-read");
        Assert.NotNull(caller);
        await using var contextHarness = await fixture.CreateAsync("membership-diagnostic-context", person.Actor);
        var context = contextHarness.Services.GetRequiredService<ITenantContext>();
        await using var source = ApplicationDataSource.Build(fixture.AppConnectionString, sinks.Loggers);
        await using var unit = new NpgsqlUnitOfWork(source, sinks.Loggers.CreateLogger<NpgsqlUnitOfWork>());
        await using var frame = await unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await unit.SetTenantContextAsync(context);
        var witness = new ReadFailureWitness();
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(unit.Connection)
            .UseLoggerFactory(sinks.Loggers).AddInterceptors(witness).Options, new StaticTenantContextAccessor(context), EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(unit.Transaction);
        var reader = new MembershipAdministrationReader(db, unit, context);
        await ExecuteAsync(owner, null, $$"""
            CREATE FUNCTION membership_admin_read_sink_fault() RETURNS boolean
            LANGUAGE plpgsql SECURITY INVOKER SET search_path=pg_catalog AS $$
            DECLARE payload text;
            BEGIN
                SELECT display_name INTO STRICT payload FROM public.membership_profiles WHERE membership_id='{{person.Membership:D}}'::uuid;
                RAISE EXCEPTION USING ERRCODE='P0399',MESSAGE=payload;
            END $$;
            REVOKE ALL ON FUNCTION membership_admin_read_sink_fault() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION membership_admin_read_sink_fault() TO learnstack_app;
            CREATE POLICY membership_admin_read_sink_fault ON memberships AS RESTRICTIVE FOR SELECT TO learnstack_app
            USING(membership_admin_read_sink_fault());
            """);
        IdentityStorageProviderException failure;
        try
        {
            failure = await Assert.ThrowsAsync<IdentityStorageProviderException>(() => reader.ReadOwnAsync());
            Assert.Null(failure.InnerException);
            Assert.Equal(payload, Assert.IsType<PostgresException>(witness.Failure).MessageText);
            await frame.FailAsync();
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP POLICY membership_admin_read_sink_fault ON memberships; DROP FUNCTION membership_admin_read_sink_fault()");
        }
        Assert.Equal(policyBefore, await ScalarAsync<string>(owner, null, """
            SELECT jsonb_agg(jsonb_build_array(polname,pg_get_expr(polqual,polrelid),pg_get_expr(polwithcheck,polrelid)) ORDER BY polname)::text
            FROM pg_policy WHERE polrelid='public.memberships'::regclass
            """));
        caller!.Stop();
        sinks.AssertCompleteEfTraceCoverage();
        IdentityDiagnosticSinks.AssertEfCallerAncestry(sinks.Traces, caller);
        await sinks.CaptureThroughL1Async(failure);
        var captured = await sinks.ReadAsync();
        captured.AssertPositiveControls();
        captured.AssertExcludes(canaries);
    }

    private sealed class ReadFailureWitness : DbCommandInterceptor
    {
        public Exception? Failure { get; private set; }
        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Failure = eventData.Exception;
            return Task.CompletedTask;
        }
    }
}
