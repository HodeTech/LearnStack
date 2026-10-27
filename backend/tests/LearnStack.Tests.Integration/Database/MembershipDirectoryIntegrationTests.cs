using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Memberships;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MembershipDirectoryIntegrationTests(IdentityAuthorizationReaderFixture fixture)
    : IClassFixture<IdentityAuthorizationReaderFixture>
{
    [Fact]
    public async Task Actual_branch_admin_directory_pages_across_its_tenant_with_only_contextual_fields_on_ReadWrite()
    {
        var query = new ListMembershipsQuery(Limit: 2);
        var items = new List<MembershipDirectoryItem>();
        do
        {
            await using var harness = await DirectoryHarness.CreateAsync(fixture, query);
            Assert.Equal(ProtectedOperationOutcome.Allowed, harness.Decision.Outcome);
            await harness.EnterAsync();
            await SchemaQueries.AssertApplicationRoleAsync((NpgsqlConnection)harness.Unit.Connection);
            Assert.Equal(TransactionMode.ReadWrite, harness.Unit.Mode);
            Assert.Equal("off", await ScalarAsync<string>((NpgsqlConnection)harness.Unit.Connection,
                (NpgsqlTransaction)harness.Unit.Transaction!, "SHOW transaction_read_only"));
            Assert.Equal("tenant", await ScalarAsync<string>((NpgsqlConnection)harness.Unit.Connection,
                (NpgsqlTransaction)harness.Unit.Transaction!, "SELECT current_setting('app.scope')"));
            Assert.True(harness.Db.CurrentTenantWideRead);
            var result = await harness.Handler.Handle(query, CancellationToken.None);
            Assert.True(result.IsSuccess);
            items.AddRange(result.Value.Items);
            query = query with { Cursor = result.Value.PageInfo.NextCursor };
            Assert.Empty(harness.Db.ChangeTracker.Entries());
        } while (query.Cursor is not null);
        Assert.Equal(new[] { AdminMembershipA, LearnerMembershipA, BranchMembershipA1, BranchMembershipA2 }.Order(),
            items.Select(item => item.MembershipId));
        Assert.Equal(4, items.Select(item => item.MembershipId).Distinct().Count());
        Assert.All(items, item =>
        {
            Assert.Equal("profile-canary", item.DisplayName);
            Assert.Equal("en", item.PreferredLocale);
            Assert.DoesNotContain(item.UserId.Value, new[] { AdminB, PersonB });
        });
        Assert.Equal(["MembershipId", "UserId", "OrganizationId", "Status", "DisplayName", "PreferredLocale"],
            typeof(MembershipDirectoryItem).GetProperties().Select(property => property.Name));
    }

    [Fact]
    public async Task Directory_includes_suspended_members_and_excludes_committed_revoked_rows()
    {
        var suspended = await fixture.AddMemberAsync(SchemaFixture.OrgA1);
        var revoked = await fixture.AddMemberAsync(SchemaFixture.OrgA2);
        try
        {
            await ChangeStatusAsync(suspended.Actor, suspended.Membership, SchemaFixture.OrgA1, "Suspended");
            await ChangeStatusAsync(revoked.Actor, revoked.Membership, SchemaFixture.OrgA2, "Revoked");
            var query = new ListMembershipsQuery();
            await using var harness = await DirectoryHarness.CreateAsync(fixture, query);
            await harness.EnterAsync();
            var result = await harness.Handler.Handle(query, CancellationToken.None);
            Assert.True(result.IsSuccess);
            Assert.Equal(DirectoryMembershipStatus.Suspended,
                Assert.Single(result.Value.Items, item => item.MembershipId == suspended.Membership).Status);
            Assert.DoesNotContain(result.Value.Items, item => item.MembershipId == revoked.Membership);
        }
        finally
        {
            await ChangeStatusAsync(suspended.Actor, suspended.Membership, SchemaFixture.OrgA1, "Revoked");
            await ChangeStatusAsync(revoked.Actor, revoked.Membership, SchemaFixture.OrgA2, "Revoked");
        }
    }

    [Fact]
    public async Task Actual_coverage_without_Tenant_admin_and_foreign_actor_refuse_before_business_checkout()
    {
        await using var learner = await DirectoryHarness.CreateAsync(fixture, new ListMembershipsQuery(), PersonA);
        Assert.Equal(ProtectedOperationOutcome.Forbidden, learner.Decision.Outcome);
        Assert.False(learner.Unit.HasActiveTransaction);
        Assert.Empty(learner.Decision.MembershipIds);
        await using var foreign = await DirectoryHarness.CreateAsync(fixture, new ListMembershipsQuery(), AdminB);
        Assert.Equal(ProtectedOperationOutcome.NotCovered, foreign.Decision.Outcome);
        Assert.False(foreign.Unit.HasActiveTransaction);
        Assert.Empty(foreign.Decision.MembershipIds);
    }

    [Fact]
    public async Task Adapter_refuses_narrow_owner_even_if_SQL_scope_is_tampered_and_real_EF_filter_stays_narrow()
    {
        await using var harness = await DirectoryHarness.CreateAsync(fixture, new ListMembershipsQuery());
        await harness.EnterAsync(wide: false);
        Assert.False(harness.Db.CurrentTenantWideRead);
        Assert.Equal(3, await harness.Db.Memberships.CountAsync(member => member.DeletedAt == null));
        await ExecuteAsync((NpgsqlConnection)harness.Unit.Connection, (NpgsqlTransaction)harness.Unit.Transaction!,
            "SELECT set_config('app.scope','tenant',true)");
        Assert.Equal(4, await ScalarAsync<long>((NpgsqlConnection)harness.Unit.Connection, (NpgsqlTransaction)harness.Unit.Transaction!,
            "SELECT count(*) FROM memberships WHERE deleted_at IS NULL"));
        Assert.Equal(3, await harness.Db.Memberships.CountAsync(member => member.DeletedAt == null));
        await Assert.ThrowsAsync<TenantContextMissingException>(() => harness.DirectoryReader.ReadAsync(null, 21));
    }

    [Fact]
    public async Task Modified_anchor_remains_a_seek_only_and_other_branch_cursor_is_validation_failure()
    {
        var scope = MembershipDirectoryCursorCodec.Scope(UserId.From(AdminA), TenantId.From(SchemaFixture.TenantA),
            OrganizationId.From(SchemaFixture.OrgA1));
        var query = new ListMembershipsQuery(MembershipDirectoryCursorCodec.Encode(scope,
            LearnStack.Modules.Identity.Domain.MembershipId.From(BranchMembershipA1)));
        await using var harness = await DirectoryHarness.CreateAsync(fixture, query);
        await harness.EnterAsync();
        var result = await harness.Handler.Handle(query, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal([BranchMembershipA2], result.Value.Items.Select(item => item.MembershipId));
        var foreignScope = MembershipDirectoryCursorCodec.Scope(UserId.From(AdminA), TenantId.From(SchemaFixture.TenantA),
            OrganizationId.From(SchemaFixture.OrgA2));
        var foreign = query with
        {
            Cursor = MembershipDirectoryCursorCodec.Encode(foreignScope,
            LearnStack.Modules.Identity.Domain.MembershipId.From(BranchMembershipA1))
        };
        var refused = await harness.Handler.Handle(foreign, CancellationToken.None);
        Assert.Equal("validation_failed", refused.Error!.Code);
        Assert.True(refused.Error.Details!.ContainsKey("cursor"));
    }

    private async Task ChangeStatusAsync(Guid actor, Guid member, Guid organization, string status)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, organization);
        await LockAsync(app, transaction, SchemaFixture.TenantA, actor, false);
        await ExecuteAsync(app, transaction, """
            UPDATE memberships SET status=@status,
                deleted_at=CASE WHEN @status='Revoked' THEN COALESCE(deleted_at,clock_timestamp()) ELSE NULL END,
                deleted_by=CASE WHEN @status='Revoked' THEN @actor ELSE NULL END
            WHERE id=@member AND status<>@status
            """, ("status", status), ("actor", UserId.SystemActor.Value), ("member", member));
        await transaction.CommitAsync();
    }

    private sealed class DirectoryHarness : IAsyncDisposable
    {
        private readonly AuthorizationReaderHarness _authorization;
        private readonly NpgsqlDataSource _data;
        private readonly CandidateSource _source;
        private readonly InvocationSource _invocations;
        private ServiceProvider? _provider;
        private IUnitOfWorkScope? _frame;

        private DirectoryHarness(AuthorizationReaderHarness authorization, NpgsqlDataSource data, NpgsqlUnitOfWork unit,
            CandidateSource source, InvocationSource invocations, ITenantContext context, ProtectedOperationDecision decision)
        {
            _authorization = authorization; _data = data; Unit = unit; _source = source;
            _invocations = invocations; Context = context; Decision = decision;
        }
        public NpgsqlUnitOfWork Unit { get; }
        public ITenantContext Context { get; }
        public ProtectedOperationDecision Decision { get; }
        public IdentityDbContext Db => _provider!.GetRequiredService<IdentityDbContext>();
        public MembershipDirectoryReader DirectoryReader => new(Db, Unit, Context);
        public ListMembershipsQueryHandler Handler => new(DirectoryReader, Context);

        public static async Task<DirectoryHarness> CreateAsync(IdentityAuthorizationReaderFixture fixture, ListMembershipsQuery query,
            Guid? actor = null)
        {
            var authorization = await AuthorizationReaderHarness.CreateAsync(fixture);
            var data = ApplicationDataSource.Build(fixture.AppConnectionString);
            var unit = new NpgsqlUnitOfWork(data, NullLogger<NpgsqlUnitOfWork>.Instance);
            var context = TenantContextFactory.Create(new TenantResolutionAttempt
            {
                HasValidatedPrincipal = true,
                UserId = UserId.From(actor ?? AdminA),
                HostTenantId = TenantId.From(SchemaFixture.TenantA),
                HostOrganizationId = OrganizationId.From(SchemaFixture.OrgA1),
                ClaimTenantId = TenantId.From(SchemaFixture.TenantA),
                ClaimOrganizationId = OrganizationId.From(SchemaFixture.OrgA1),
                MembershipCovers = true,
                ClaimedOrganizationBelongsToTenant = true,
            }).Value;
            Assert.NotNull(context);
            var source = new CandidateSource();
            var candidate = source.Mint(context);
            var catalog = new OperationPolicyCatalog([new IdentityOperationPolicySource()]);
            Assert.True(catalog.TryGet(typeof(ListMembershipsQuery), out var policy));
            var invocations = new InvocationSource();
            var invocation = invocations.Mint(query, policy, context, candidate);
            var decision = await new IdentityOperationAuthorizer(authorization.Reader, catalog).AuthorizeAsync(invocation, policy, candidate);
            return new(authorization, data, unit, source, invocations, context, decision);
        }

        public async Task EnterAsync(bool wide = true)
        {
            _frame = await Unit.BeginTransactionAsync();
            if (wide) await Unit.SetTenantContextAsync(Context, Decision.ReadWitness!);
            else await Unit.SetTenantContextAsync(Context);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IUnitOfWork>(Unit);
            services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(Context));
            services.AddSingleton<IGlobalIdentityScopeAccessor>(new GlobalIdentityScopeAccessor(Unit));
            services.AddModuleDbContext<IdentityDbContext>();
            _provider = services.BuildServiceProvider();
        }

        public async ValueTask DisposeAsync()
        {
            if (_provider is not null) await _provider.DisposeAsync();
            if (_frame is not null) await _frame.DisposeAsync();
            await Unit.DisposeAsync();
            await _data.DisposeAsync();
            _invocations.Close();
            _source.Dispose();
            await _authorization.DisposeAsync();
        }
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource
    {
        public AuthorizationCandidate Mint(TenantContext context) =>
            ForCandidate(AuthorizationSourceKind.Http, context.UserId!.Value, context.TenantId, context.OrganizationId);
    }
    private sealed class InvocationSource : OperationInvocation.MintingSource
    {
        private OperationInvocation? _current;
        public OperationInvocation Mint(object operation, OperationPolicy policy, ITenantContext context, AuthorizationCandidate candidate)
        {
            _current = ForInvocation(operation, policy, context, candidate, null, CancellationToken.None);
            return _current;
        }
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(invocation, _current);
        public void Close() => _current = null;
    }
}
