using System.Data;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentityAuthorizationReaderTests(IdentityAuthorizationReaderFixture fixture)
    : IClassFixture<IdentityAuthorizationReaderFixture>
{
    [Fact]
    public async Task Exact_tenant_and_branch_compose_only_their_active_registered_facts_without_personal_projection()
    {
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
        using var minter = new CandidateSource();
        var result = await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA1));

        result.Outcome.Should().Be(IdentityAuthorizationOutcome.Covered);
        result.Memberships.Select(member => member.MembershipId).Should().BeEquivalentTo([LearnerMembershipA, BranchMembershipA1]);
        result.Memberships.SelectMany(member => member.Roles).SelectMany(role => role.Grants).Should().BeEquivalentTo([
            new PermissionDefinition(PermissionScope.Tenant, IdentityFoundationManifest.ProfileWrite),
            new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite)]);
        var encoded = JsonSerializer.Serialize(result);
        encoded.Should().NotContain("profile-canary").And.NotContain("fixture-subject-")
            .And.NotContain("DisplayName").And.NotContain("PreferredLocale");

        var orgless = await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA));
        orgless.Memberships.Should().ContainSingle().Which.MembershipId.Should().Be(LearnerMembershipA);
        var otherBranch = await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA2));
        otherBranch.Memberships.Select(member => member.MembershipId).Should().BeEquivalentTo([LearnerMembershipA, BranchMembershipA2]);
    }

    [Fact]
    public async Task Foreign_actor_tenant_or_organization_never_supplies_coverage()
    {
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
        using var minter = new CandidateSource();
        foreach (var candidate in new[]
        {
            minter.For(AdminB, SchemaFixture.TenantA),
            minter.For(PersonA, SchemaFixture.TenantB),
            minter.For(PersonA, SchemaFixture.TenantA, SchemaFixture.OrgB1),
            minter.For(PersonA, Guid.CreateVersion7()),
        })
        {
            var result = await harness.Reader.ReadAsync(candidate);
            result.Outcome.Should().Be(IdentityAuthorizationOutcome.NotCovered);
            result.Memberships.Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Membership_without_any_role_or_grant_still_confirms_coverage()
    {
        var (actor, member) = await fixture.AddMemberAsync(roleKey: BuiltInRoleKey.Instructor);
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
        using var minter = new CandidateSource();
        var emptyRole = await harness.Reader.ReadAsync(minter.For(actor, SchemaFixture.TenantA));
        emptyRole.Covers.Should().BeTrue();
        emptyRole.Memberships.Single().Roles.Single().Grants.Should().BeEmpty();
        await MutateAsync(null, actor, "DELETE FROM public.membership_role_assignments WHERE membership_id=@member", ("member", member));
        var noRole = await harness.Reader.ReadAsync(minter.For(actor, SchemaFixture.TenantA));
        noRole.Covers.Should().BeTrue();
        noRole.Memberships.Single().Roles.Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_and_virtual_public_stored_grants_never_become_assignable_authority()
    {
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.Postgres.MigrationConnectionString);
        // Deliberate corrupt/legacy rows: the act under test remains the real qualified application-role reader.
        await ExecuteAsync(owner, null, "ALTER TABLE public.role_permission_grants DROP CONSTRAINT ck_role_permission_grants_registry");
        try
        {
            await MutateAsync(null, null, """
                INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                VALUES(@tenant,@role,'Tenant','identity.unknown.read'),(@tenant,@role,'Tenant','education.public_course.read')
                """, ("tenant", SchemaFixture.TenantA), ("role", role));
            await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
            using var minter = new CandidateSource();
            var result = await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA));
            result.Covers.Should().BeTrue();
            result.Memberships.Single().Roles.Single().Grants.Should().BeEquivalentTo([
                new PermissionDefinition(PermissionScope.Tenant, IdentityFoundationManifest.ProfileWrite)]);
        }
        finally
        {
            await MutateAsync(null, null, """
                DELETE FROM public.role_permission_grants WHERE role_id=@role
                  AND permission_key IN ('identity.unknown.read','education.public_course.read')
                """, ("role", role));
            await ExecuteAsync(owner, null, $"""
                ALTER TABLE public.role_permission_grants ADD CONSTRAINT ck_role_permission_grants_registry
                    CHECK ((permission_scope,permission_key) IN ({IdentityFoundationManifest.Version1.RegistrySqlValues}))
                """);
        }
    }

    [Fact]
    public async Task Excess_or_ambiguous_committed_memberships_fail_closed_instead_of_truncating_the_graph()
    {
        var (actor, _) = await fixture.AddMemberAsync();
        var firstExtra = Guid.CreateVersion7();
        var secondExtra = Guid.CreateVersion7();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.Postgres.MigrationConnectionString);
        await ExecuteAsync(owner, null, "DROP INDEX public.ux_memberships_current_scope");
        try
        {
            await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString))
            await using (var transaction = await app.BeginTransactionAsync())
            {
                await ScopeAsync(app, transaction, SchemaFixture.TenantA);
                await LockAsync(app, transaction, SchemaFixture.TenantA, actor, false);
                foreach (var member in new[] { firstExtra, secondExtra })
                    await InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, member, actor,
                        RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
                await transaction.CommitAsync();
            }
            await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
            using var minter = new CandidateSource();
            var result = await harness.Reader.ReadAsync(minter.For(actor, SchemaFixture.TenantA));
            result.Outcome.Should().Be(IdentityAuthorizationOutcome.Unavailable);
            result.Memberships.Should().BeEmpty();
        }
        finally
        {
            await MutateAsync(null, null, """
                UPDATE public.memberships SET status='Revoked',deleted_at=clock_timestamp(),deleted_by=@actor
                WHERE id IN (@first,@second)
                """, ("actor", UserId.SystemActor.Value), ("first", firstExtra), ("second", secondExtra));
            await ExecuteAsync(owner, null, """
                CREATE UNIQUE INDEX ux_memberships_current_scope ON public.memberships(tenant_id,user_id,organization_id)
                    NULLS NOT DISTINCT WHERE deleted_at IS NULL
                """);
        }
    }

    [Fact]
    public async Task Entire_finite_eight_role_population_is_returned_without_implicit_grants()
    {
        var added = IdentityFoundationManifest.Version1.Templates
            .Where(template => template.BuiltInKey != BuiltInRoleKey.Learner).ToArray();
        try
        {
            foreach (var template in added) await ChangeAssignmentAsync(template, add: true);
            await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
            using var minter = new CandidateSource();
            var result = await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA1));
            result.Covers.Should().BeTrue();
            var roles = result.Memberships.SelectMany(member => member.Roles).ToArray();
            roles.Should().HaveCount(8);
            foreach (var role in roles)
                role.Grants.Should().BeEquivalentTo(IdentityFoundationManifest.Version1.FindTemplate(role.BuiltInKey, role.Scope)!.Grants);
        }
        finally
        {
            foreach (var template in added.Reverse()) await ChangeAssignmentAsync(template, add: false);
        }
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Revoked")]
    public async Task Fresh_committed_suspended_or_revoked_membership_has_no_coverage(string status)
    {
        var (actor, member) = await fixture.AddMemberAsync();
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
        using var minter = new CandidateSource();
        (await harness.Reader.ReadAsync(minter.For(actor, SchemaFixture.TenantA))).Covers.Should().BeTrue();
        await MutateAsync(null, actor, """
            UPDATE public.memberships SET status=@status,
                deleted_at=CASE WHEN @status='Revoked' THEN clock_timestamp() ELSE NULL END,
                deleted_by=CASE WHEN @status='Revoked' THEN @actor ELSE NULL END
            WHERE id=@member
            """, ("status", status), ("actor", UserId.SystemActor.Value), ("member", member));
        var result = await harness.Reader.ReadAsync(minter.For(actor, SchemaFixture.TenantA));
        result.Outcome.Should().Be(IdentityAuthorizationOutcome.NotCovered);
        result.Memberships.Should().BeEmpty();
    }

    [Fact]
    public async Task Retired_role_and_removed_assignment_grant_nothing_while_membership_coverage_remains()
    {
        var (actor, member) = await fixture.AddMemberAsync();
        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant);
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
        using var minter = new CandidateSource();
        try
        {
            await MutateAsync(null, null,
                "UPDATE public.roles SET deleted_at=clock_timestamp(),deleted_by=@actor WHERE id=@role",
                ("actor", UserId.SystemActor.Value), ("role", role));
            var retired = await harness.Reader.ReadAsync(minter.For(actor, SchemaFixture.TenantA));
            retired.Covers.Should().BeTrue();
            retired.Memberships.Single().Roles.Should().BeEmpty();
        }
        finally
        {
            await MutateAsync(null, null, "UPDATE public.roles SET deleted_at=NULL,deleted_by=NULL WHERE id=@role", ("role", role));
        }
        await MutateAsync(null, actor, "DELETE FROM public.membership_role_assignments WHERE membership_id=@member", ("member", member));
        (await harness.Reader.ReadAsync(minter.For(actor, SchemaFixture.TenantA))).Memberships.Single().Roles.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Trial", true)]
    [InlineData("Active", true)]
    [InlineData("Suspended", false)]
    [InlineData("Archived", false)]
    public async Task Actual_tenant_lifecycle_is_fresh_and_independent_of_membership(string status, bool covers)
    {
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
        using var minter = new CandidateSource();
        try
        {
            await MutateAsync(null, null, "UPDATE public.tenants SET status=@status WHERE id=@tenant",
                ("status", status), ("tenant", SchemaFixture.TenantA));
            (await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA))).Covers.Should().Be(covers);
        }
        finally
        {
            await MutateAsync(null, null, "UPDATE public.tenants SET status='Trial' WHERE id=@tenant", ("tenant", SchemaFixture.TenantA));
        }
    }

    [Theory]
    [InlineData("Active", true)]
    [InlineData("Suspended", false)]
    [InlineData("Archived", false)]
    public async Task Requested_organization_must_currently_allow_work(string status, bool covers)
    {
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
        using var minter = new CandidateSource();
        try
        {
            await MutateAsync(SchemaFixture.OrgA1, null, "UPDATE public.organizations SET status=@status WHERE id=@organization",
                ("status", status), ("organization", SchemaFixture.OrgA1));
            (await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA1))).Covers.Should().Be(covers);
        }
        finally
        {
            await MutateAsync(SchemaFixture.OrgA1, null, "UPDATE public.organizations SET status='Active' WHERE id=@organization",
                ("organization", SchemaFixture.OrgA1));
        }
    }

    [Fact]
    public async Task Separate_lifecycle_statement_observes_a_commit_after_the_grant_statement()
    {
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture, inner => new LifecycleProbe(inner,
            async (_, _, _) => await MutateAsync(null, null, "UPDATE public.tenants SET status='Suspended' WHERE id=@tenant",
                ("tenant", SchemaFixture.TenantA))));
        using var minter = new CandidateSource();
        try
        {
            (await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA))).Outcome.Should().Be(IdentityAuthorizationOutcome.NotCovered);
        }
        finally
        {
            await MutateAsync(null, null, "UPDATE public.tenants SET status='Trial' WHERE id=@tenant", ("tenant", SchemaFixture.TenantA));
        }
    }

    [Fact]
    public async Task Child_is_physically_read_only_and_its_exact_generation_is_the_lifecycle_generation()
    {
        TenantContextAnnouncement? observed = null;
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture, inner => new LifecycleProbe(inner,
            async (child, candidate, cancellationToken) =>
            {
                child.Mode.Should().Be(TransactionMode.ReadOnly);
                observed = child.GetTenantContextAnnouncement(child.Transaction);
                observed!.Generation.Should().BePositive();
                observed.TenantId.Should().Be(candidate.TenantId);
                observed.OrganizationId.Should().Be(candidate.OrganizationId);
                await using var command = new NpgsqlCommand("SELECT current_setting('transaction_read_only')",
                    (NpgsqlConnection)child.Connection, (NpgsqlTransaction)child.Transaction!);
                (await command.ExecuteScalarAsync(cancellationToken)).Should().Be("on");
            }));
        using var minter = new CandidateSource();
        (await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA1))).Covers.Should().BeTrue();
        observed.Should().NotBeNull();
    }

    [Fact]
    public async Task Stale_candidate_before_or_during_io_never_returns_facts()
    {
        using var minter = new CandidateSource();
        var candidate = minter.For(PersonA, SchemaFixture.TenantA);
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture, inner => new LifecycleProbe(inner,
            (_, _, _) => { minter.Dispose(); return Task.CompletedTask; }));
        (await harness.Reader.ReadAsync(candidate)).Outcome.Should().Be(IdentityAuthorizationOutcome.Unavailable);
        (await harness.Reader.ReadAsync(candidate)).Memberships.Should().BeEmpty();
    }

    [Fact]
    public async Task Candidate_lifetime_ending_during_actual_cleanup_withholds_already_prepared_facts()
    {
        using var minter = new CandidateSource();
        var candidate = minter.For(PersonA, SchemaFixture.TenantA);
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture, inner => new LifecycleProbe(inner,
            (child, _, _) =>
            {
                child.Connection.StateChange += (_, args) =>
                {
                    if (args.CurrentState == ConnectionState.Closed) minter.Dispose();
                };
                return Task.CompletedTask;
            }));
        var result = await harness.Reader.ReadAsync(candidate);
        result.Outcome.Should().Be(IdentityAuthorizationOutcome.Unavailable);
        result.Memberships.Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_committed_revocation_is_seen_while_parent_uncommitted_grant_and_locks_remain_open()
    {
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture);
        using var minter = new CandidateSource();
        var tenantRole = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant);
        var branchRole = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Organization);
        await using var parent = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var parentTransaction = await parent.BeginTransactionAsync();
        await ScopeAsync(parent, parentTransaction, SchemaFixture.TenantA);
        await LockAsync(parent, parentTransaction, SchemaFixture.TenantA, null, false);
        await ExecuteAsync(parent, parentTransaction, """
            INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
            VALUES(@tenant,@role,'Organization',@key)
            """, ("tenant", SchemaFixture.TenantA), ("role", branchRole), ("key", IdentityFoundationManifest.MembershipAdmin));
        try
        {
            // This separate app owner has committed its revocation before the candidate's grant query starts.
            await MutateAsync(null, null, "DELETE FROM public.role_permission_grants WHERE role_id=@role AND permission_key=@key",
                ("role", tenantRole), ("key", IdentityFoundationManifest.ProfileWrite));
            var result = await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA, SchemaFixture.OrgA1));
            result.Covers.Should().BeTrue();
            result.Memberships.SelectMany(member => member.Roles).SelectMany(role => role.Grants).Should().BeEquivalentTo([
                new PermissionDefinition(PermissionScope.Organization, IdentityFoundationManifest.ProfileWrite)]);
            parentTransaction.Connection.Should().BeSameAs(parent);
        }
        finally
        {
            await parentTransaction.RollbackAsync();
            await MutateAsync(null, null, """
                INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                VALUES(@tenant,@role,'Tenant',@key)
                """, ("tenant", SchemaFixture.TenantA), ("role", tenantRole), ("key", IdentityFoundationManifest.ProfileWrite));
        }
    }

    [Fact]
    public async Task Request_cancellation_propagates_after_owned_cleanup_and_the_slot_recovers()
    {
        using var cancellation = new CancellationTokenSource();
        var cancelOnce = true;
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture, inner => new LifecycleProbe(inner,
            (_, _, _) => { if (cancelOnce) { cancelOnce = false; cancellation.Cancel(); } return Task.CompletedTask; }));
        using var minter = new CandidateSource();
        await FluentActions.Awaiting(() => harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA), cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        (await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA))).Covers.Should().BeTrue();
    }

    [Fact]
    public async Task Internal_deadline_is_unavailable_with_no_authority_payload()
    {
        await using var harness = await AuthorizationReaderHarness.CreateAsync(fixture, inner => new LifecycleProbe(inner,
            async (_, _, cancellationToken) => await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)));
        using var minter = new CandidateSource();
        var result = await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA));
        result.Outcome.Should().Be(IdentityAuthorizationOutcome.Unavailable);
        result.Memberships.Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Lifecycle_storage_or_reported_actual_cleanup_failure_never_publishes_prepared_facts(bool cleanupFailed)
    {
        var harness = await AuthorizationReaderHarness.CreateAsync(fixture, inner => new LifecycleProbe(inner,
            (_, _, _) => throw new IdentityScopeLifecycleReadException(cleanupFailed)));
        using var minter = new CandidateSource();
        try
        {
            var result = await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA));
            result.Outcome.Should().Be(IdentityAuthorizationOutcome.Unavailable);
            result.Memberships.Should().BeEmpty();
            var owners = new List<AuthorizationReadOwner>();
            try
            {
                for (var index = 0; index < (cleanupFailed ? 3 : 4); index++)
                    owners.Add((await harness.Source.OpenAsync(minter.For(PersonA, SchemaFixture.TenantA)))!);
                (await harness.Source.OpenAsync(minter.For(PersonA, SchemaFixture.TenantA))).Should().BeNull();
            }
            finally { foreach (var owner in owners) await owner.DisposeAsync(); }
        }
        finally
        {
            if (cleanupFailed)
                await FluentActions.Awaiting(async () => await harness.DisposeAsync()).Should().ThrowAsync<InvalidOperationException>();
            else await harness.DisposeAsync();
        }
    }

    [Fact]
    public async Task Actual_connection_disposal_fault_withholds_prepared_facts_and_retains_capacity()
    {
        var harness = await AuthorizationReaderHarness.CreateAsync(fixture, inner => new LifecycleProbe(inner,
            (child, _, _) =>
            {
                child.Connection.StateChange += (_, args) =>
                {
                    if (args.CurrentState == ConnectionState.Closed) throw new IOException("cleanup-sensitive-canary");
                };
                return Task.CompletedTask;
            }));
        using var minter = new CandidateSource();
        try
        {
            var result = await harness.Reader.ReadAsync(minter.For(PersonA, SchemaFixture.TenantA));
            result.Outcome.Should().Be(IdentityAuthorizationOutcome.Unavailable);
            result.Memberships.Should().BeEmpty();
            var candidate = minter.For(PersonA, SchemaFixture.TenantA);
            var owners = new List<AuthorizationReadOwner>();
            try
            {
                for (var index = 0; index < 3; index++) owners.Add((await harness.Source.OpenAsync(candidate))!);
                (await harness.Source.OpenAsync(candidate)).Should().BeNull();
            }
            finally { foreach (var owner in owners) await owner.DisposeAsync(); }
        }
        finally
        {
            await FluentActions.Awaiting(async () => await harness.DisposeAsync()).Should().ThrowAsync<IOException>();
        }
    }

    [Fact]
    public async Task Lifecycle_authorization_port_refuses_foreign_announcement_and_stale_physical_transaction()
    {
        await using var business = ApplicationDataSource.Build(fixture.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(business, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await unit.SetTenantContextAsync(new Context(TenantId.From(SchemaFixture.TenantA), null));
        var reader = new IdentityScopeLifecycleReader(unit);
        using var minter = new CandidateSource();
        await FluentActions.Awaiting(() => reader.ReadAuthorizationAsync(unit, minter.For(PersonA, SchemaFixture.TenantB)))
            .Should().ThrowAsync<IdentityScopeLifecycleReadException>();
        await frame.CompleteAsync();
        await FluentActions.Awaiting(() => reader.ReadAuthorizationAsync(unit, minter.For(PersonA, SchemaFixture.TenantA)))
            .Should().ThrowAsync<IdentityScopeLifecycleReadException>();
    }

    private async Task MutateAsync(Guid? organization, Guid? actor, string sql, params (string Name, object? Value)[] values)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
        await LockAsync(app, transaction, SchemaFixture.TenantA, actor, false);
        await ExecuteAsync(app, transaction, sql, values);
        await transaction.CommitAsync();
    }

    private async Task ChangeAssignmentAsync(BuiltInRoleTemplate template, bool add)
    {
        var organization = template.Scope == PermissionScope.Organization ? SchemaFixture.OrgA1 : (Guid?)null;
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
        await LockAsync(app, transaction, SchemaFixture.TenantA, add ? PersonA : null, template.BuiltInKey == BuiltInRoleKey.TenantAdmin);
        await ExecuteAsync(app, transaction, add ? """
            INSERT INTO public.membership_role_assignments(tenant_id,membership_id,role_id,organization_id)
            VALUES(@tenant,@member,@role,@organization)
            """ : "DELETE FROM public.membership_role_assignments WHERE membership_id=@member AND role_id=@role",
            ("tenant", SchemaFixture.TenantA), ("member", organization is null ? LearnerMembershipA : BranchMembershipA1),
            ("role", RoleId(SchemaFixture.TenantA, template.BuiltInKey, template.Scope)), ("organization", organization));
        await transaction.CommitAsync();
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource
    {
        public AuthorizationCandidate For(Guid actor, Guid tenant, Guid? organization = null) =>
            ForCandidate(AuthorizationSourceKind.Http, UserId.From(actor), TenantId.From(tenant),
                organization is { } org ? OrganizationId.From(org) : null);
    }

    private sealed class LifecycleProbe(IIdentityScopeLifecycleReader inner,
        Func<IUnitOfWork, AuthorizationCandidate, CancellationToken, Task> before) : IIdentityScopeLifecycleReader
    {
        public Task<IdentityScopeLifecycle> ReadAsync(TenantId tenantId, OrganizationId? organizationId,
            CancellationToken cancellationToken = default) => inner.ReadAsync(tenantId, organizationId, cancellationToken);
        public async Task<IdentityScopeLifecycle> ReadAuthorizationAsync(IUnitOfWork child, AuthorizationCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            await before(child, candidate, cancellationToken);
            return await inner.ReadAuthorizationAsync(child, candidate, cancellationToken);
        }
    }

    private sealed record Context(TenantId TenantId, OrganizationId? OrganizationId) : ITenantContext
    {
        public bool IsResolved => true;
        public UserId? UserId => null;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }
}
