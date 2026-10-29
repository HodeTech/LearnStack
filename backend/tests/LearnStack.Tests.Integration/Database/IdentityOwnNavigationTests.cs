using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Memberships;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentityOwnNavigationTests(IdentityOwnNavigationFixture fixture) : IClassFixture<IdentityOwnNavigationFixture>
{
    [Fact]
    public async Task Escaped_context_pages_fit_MVC_and_SDK_bytes_without_losing_ordered_continuation_or_small_pages()
    {
        var organizations = Enumerable.Range(0, 115).Select(_ => Guid.CreateVersion7()).ToArray();
        var label = new string('\u0001', MappedLength.DisplayName);
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.True(JsonValue.IsStorableText(label));
        var originalName = await fixture.TenantNameAsync();
        try
        {
            await fixture.SeedPageAsync(organizations, label);
            var expected = new Guid?[] { null }.Concat(organizations.Append(SchemaFixture.OrgA1).Append(SchemaFixture.OrgA2)
                .OrderBy(id => id.ToString("D"), StringComparer.Ordinal).Select(id => (Guid?)id)).ToArray();
            await ReadAllAsync("own-contexts-escaped-page", expected, expectFullPage: false);

            await using (var one = await fixture.CreateAsync("own-contexts-escaped-one", AdminA, SchemaFixture.OrgA1))
            {
                var tenant = await one.Sender.Send(new GetOwnMembershipContextsQuery(Limit: 1));
                Assert.True(tenant.IsSuccess);
                Assert.Null(Assert.Single(tenant.Value.Items).OrganizationId);
                Assert.True(tenant.Value.PageInfo.HasNext);
                var branch = await one.Sender.Send(new GetOwnMembershipContextsQuery(tenant.Value.PageInfo.NextCursor, 1));
                Assert.True(branch.IsSuccess);
                Assert.NotNull(Assert.Single(branch.Value.Items).OrganizationId);
                Assert.Equal(label, branch.Value.Items[0].OrganizationName);
                AssertWire(branch.Value);
            }

            await fixture.SetPageLabelsAsync(organizations, "Small label");
            await ReadAllAsync("own-contexts-small-page", expected, expectFullPage: true);
        }
        finally { await fixture.ClearPageAsync(organizations, originalName); }

        async Task ReadAllAsync(string correlation, Guid?[] expected, bool expectFullPage)
        {
            await using var harness = await fixture.CreateAsync(correlation, AdminA, SchemaFixture.OrgA1);
            var seen = new List<Guid?>();
            var cursors = new HashSet<string>(StringComparer.Ordinal);
            string? cursor = null;
            var pages = 0;
            do
            {
                var result = await harness.Sender.Send(new GetOwnMembershipContextsQuery(cursor, 100));
                Assert.True(result.IsSuccess);
                var page = result.Value;
                Assert.InRange(page.Items.Count, 1, 100);
                if (pages == 0)
                {
                    if (expectFullPage) Assert.Equal(100, page.Items.Count);
                    else Assert.InRange(page.Items.Count, 1, 99);
                }
                AssertWire(page);
                Assert.All(page.Items, item => Assert.Equal(TenantId.From(SchemaFixture.TenantA), item.TenantId));
                foreach (var item in page.Items.Where(item => item.OrganizationId is { } id && organizations.Contains(id.Value)))
                    Assert.Equal(OwnNavigationWireProof.LongHost(item.OrganizationId!.Value.Value), item.EntryHost);
                seen.AddRange(page.Items.Select(item => item.OrganizationId?.Value));
                Assert.Equal(page.PageInfo.NextCursor is not null, page.PageInfo.HasNext);
                cursor = page.PageInfo.NextCursor;
                if (cursor is not null) Assert.True(cursors.Add(cursor));
                Assert.False(harness.Unit.HasActiveTransaction);
                Assert.InRange(++pages, 1, 4);
            } while (cursor is not null);
            Assert.True(pages > 1);
            Assert.Equal(expected, seen);
            Assert.Equal(seen.Count, seen.Distinct().Count());
        }

        void AssertWire(LearnStack.SharedKernel.Pagination.Page<OwnMembershipContextDto> page)
        {
            var options = fixture.Http.Services.GetRequiredService<IOptions<Microsoft.AspNetCore.Mvc.JsonOptions>>().Value.JsonSerializerOptions;
            var items = page.Items.Select(item => new OwnTenantNavigationItem(item.TenantId, item.OrganizationId,
                item.TenantName, item.OrganizationName, item.EntryHost)).ToArray();
            OwnNavigationWireProof.AssertSdkReadable(OwnNavigationWireProof.Serialize(items, page.PageInfo, options), items.Length);
        }
    }

    [Fact]
    public async Task Branch_only_coverage_returns_only_its_branch_while_Tenant_membership_derives_active_branches()
    {
        var person = await fixture.Administration.Membership.AddMemberAsync(SchemaFixture.OrgA1);
        await using (var branch = await fixture.CreateAsync("own-contexts-branch", person.Actor, SchemaFixture.OrgA1))
        {
            var result = await branch.Sender.Send(new GetOwnMembershipContextsQuery());
            Assert.True(result.IsSuccess);
            var item = Assert.Single(result.Value.Items);
            Assert.Equal(TenantId.From(SchemaFixture.TenantA), item.TenantId);
            Assert.Equal(OrganizationId.From(SchemaFixture.OrgA1), item.OrganizationId);
            Assert.Equal("own-navigation-a1.example.invalid", item.EntryHost);
        }
        await fixture.Administration.Membership.AddMembershipAsync(person.Actor, null);
        await using var tenant = await fixture.CreateAsync("own-contexts-tenant-coverage", person.Actor, SchemaFixture.OrgA1);
        var expanded = await tenant.Sender.Send(new GetOwnMembershipContextsQuery());
        Assert.True(expanded.IsSuccess);
        Assert.Equal(3, expanded.Value.Items.Count);
        Assert.Null(expanded.Value.Items[0].OrganizationId);
        Assert.Equal("own-navigation-a.example.invalid", expanded.Value.Items[0].EntryHost);
        Assert.Equal(new[] { SchemaFixture.OrgA1, SchemaFixture.OrgA2 }, expanded.Value.Items.Skip(1).Select(item => item.OrganizationId!.Value.Value));
        Assert.All(expanded.Value.Items, item => Assert.Equal(TenantId.From(SchemaFixture.TenantA), item.TenantId));
        Assert.False(tenant.Unit.HasActiveTransaction);
        Assert.Empty(await fixture.Administration.Membership.AuditAsync("own-contexts-tenant-coverage", "identity.authorization.deny", SchemaFixture.OrgA1));
        Assert.Empty(await fixture.Administration.Membership.AuditAsync("own-contexts-tenant-coverage", "tenancy.organization.list", SchemaFixture.OrgA1));
    }

    [Fact]
    public async Task Own_context_pages_bind_actor_current_scope_and_the_coverage_shape()
    {
        await using var harness = await fixture.CreateAsync("own-context-pages", AdminA, SchemaFixture.OrgA1);
        var first = await harness.Sender.Send(new GetOwnMembershipContextsQuery(Limit: 1));
        Assert.True(first.IsSuccess);
        Assert.Null(Assert.Single(first.Value.Items).OrganizationId);
        Assert.True(first.Value.PageInfo.HasNext);
        var second = await harness.Sender.Send(new GetOwnMembershipContextsQuery(first.Value.PageInfo.NextCursor, 1));
        Assert.True(second.IsSuccess);
        Assert.Equal(OrganizationId.From(SchemaFixture.OrgA1), Assert.Single(second.Value.Items).OrganizationId);
        var third = await harness.Sender.Send(new GetOwnMembershipContextsQuery(second.Value.PageInfo.NextCursor, 1));
        Assert.True(third.IsSuccess);
        Assert.Equal(OrganizationId.From(SchemaFixture.OrgA2), Assert.Single(third.Value.Items).OrganizationId);
        Assert.False(third.Value.PageInfo.HasNext);
        await using var other = await fixture.CreateAsync("own-context-pages-other", PersonA, SchemaFixture.OrgA1);
        Assert.Equal("validation_failed", (await other.Sender.Send(new GetOwnMembershipContextsQuery(first.Value.PageInfo.NextCursor, 1))).Error!.Code);
        await using var otherScope = await fixture.CreateAsync("own-context-pages-other-scope", AdminA, SchemaFixture.OrgA2);
        Assert.Equal("validation_failed", (await otherScope.Sender.Send(new GetOwnMembershipContextsQuery(first.Value.PageInfo.NextCursor, 1))).Error!.Code);
    }

    [Fact]
    public async Task Port_and_coverage_cannot_be_used_without_the_exact_current_own_query()
    {
        await using var harness = await fixture.CreateAsync("own-contexts-direct-port", AdminA, SchemaFixture.OrgA1);
        var provider = harness.Services.GetRequiredService<IOwnTenantNavigationCoverage>();
        Assert.Equal("forbidden", (await provider.ReadCurrentAsync()).Error!.Code);
        // Physical enrollment refuses even construction outside an announced owner.
        Assert.Throws<InvalidOperationException>(() => harness.Services.GetRequiredService<IOwnTenantNavigationReader>());
        Assert.False(harness.Unit.HasActiveTransaction);
        await using var frame = await harness.Unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<LearnStack.SharedKernel.Tenancy.ITenantContext>());
        var reader = harness.Services.GetRequiredService<IOwnTenantNavigationReader>();
        Assert.Equal("forbidden", (await reader.ReadAsync(null, 20)).Error!.Code);
        await frame.FailAsync();
    }

    [Fact]
    public async Task Actual_navigation_requires_its_physical_read_only_owner()
    {
        await using var harness = await fixture.CreateAsync("own-contexts-wrong-owner", AdminA, SchemaFixture.OrgA1, services =>
        {
            var registration = Assert.Single(services, descriptor => descriptor.ImplementationInstance is ExplicitReadOnlyRequest read
                && read.RequestType == typeof(GetOwnMembershipContextsQuery));
            services.Remove(registration);
        });
        Assert.Equal("forbidden", (await harness.Sender.Send(new GetOwnMembershipContextsQuery())).Error!.Code);
        Assert.False(harness.Unit.HasActiveTransaction);
    }

    [Fact]
    public async Task Another_own_query_and_an_absent_current_candidate_cannot_open_navigation()
    {
        await using (var wrongQuery = await fixture.CreateAsync("own-contexts-wrong-query", AdminA, SchemaFixture.OrgA1,
            services => services.AddScoped<IPipelineBehavior<GetOwnCapabilitiesQuery, Result<OwnCapabilitiesDto>>, WrongQueryNavigationProbe>()))
        {
            Assert.True((await wrongQuery.Sender.Send(new GetOwnCapabilitiesQuery())).IsSuccess);
        }
        await using var missingCandidate = await fixture.CreateAsync("own-contexts-no-candidate", AdminA, SchemaFixture.OrgA1,
            services => services.RemoveAll<LearnStack.SharedKernel.Permissions.IAuthorizationCandidateSource>());
        Assert.Equal("not_found", (await missingCandidate.Sender.Send(new GetOwnMembershipContextsQuery())).Error!.Code);
    }

    [Fact]
    public async Task Retained_prior_invocation_result_is_not_navigation_authority()
    {
        OwnTenantNavigationAccess? captured = null;
        await using (var initial = await fixture.CreateAsync("own-contexts-capture", AdminA, SchemaFixture.OrgA1, services =>
            services.AddScoped<IOwnTenantNavigationCoverage>(provider => new CaptureCoverage(
                new OwnTenantNavigationCoverage(provider.GetRequiredService<IdentityAdministrationReadAuthorization>(),
                    provider.GetRequiredService<LearnStack.SharedKernel.Permissions.IOperationInvocationContext>()), value => captured = value))))
        {
            Assert.True((await initial.Sender.Send(new GetOwnMembershipContextsQuery())).IsSuccess);
        }
        Assert.NotNull(captured);
        Assert.False(captured.Invocation.IsActive);
        await using var next = await fixture.CreateAsync("own-contexts-retained", AdminA, SchemaFixture.OrgA1,
            services => services.AddSingleton<IOwnTenantNavigationCoverage>(new RetainedCoverage(captured)));
        Assert.Equal("forbidden", (await next.Sender.Send(new GetOwnMembershipContextsQuery())).Error!.Code);
    }

    [Fact]
    public async Task Losing_the_Tenant_membership_during_navigation_withholds_derived_branches()
    {
        var person = await fixture.Administration.Membership.AddMemberAsync(SchemaFixture.OrgA1);
        var tenantMember = await fixture.Administration.Membership.AddMembershipAsync(person.Actor, null);
        var reached = false;
        await using (var harness = await fixture.CreateAsync("own-contexts-coverage-race", person.Actor, SchemaFixture.OrgA1, services =>
            services.AddScoped<IOwnTenantNavigationCoverage>(provider => new AfterFirstCoverage(
                new OwnTenantNavigationCoverage(provider.GetRequiredService<IdentityAdministrationReadAuthorization>(),
                    provider.GetRequiredService<LearnStack.SharedKernel.Permissions.IOperationInvocationContext>()), async () =>
                {
                    reached = true;
                    Assert.True((await fixture.Administration.SendAsync(new ChangeMembershipStatusCommand(tenantMember, 0,
                        ManagedMembershipStatus.Revoked), "own-contexts-revoke-tenant")).IsSuccess);
                }))))
        {
            Assert.Equal("not_found", (await harness.Sender.Send(new GetOwnMembershipContextsQuery())).Error!.Code);
        }
        Assert.True(reached);
        await using var branch = await fixture.CreateAsync("own-contexts-remaining-branch", person.Actor, SchemaFixture.OrgA1);
        var current = await branch.Sender.Send(new GetOwnMembershipContextsQuery());
        Assert.True(current.IsSuccess);
        Assert.Equal(OrganizationId.From(SchemaFixture.OrgA1), Assert.Single(current.Value.Items).OrganizationId);
    }

    private sealed class CaptureCoverage(IOwnTenantNavigationCoverage inner, Action<OwnTenantNavigationAccess> capture) : IOwnTenantNavigationCoverage
    {
        public async Task<Result<OwnTenantNavigationAccess>> ReadCurrentAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadCurrentAsync(cancellationToken);
            if (result.IsSuccess) capture(result.Value);
            return result;
        }
    }

    private sealed class WrongQueryNavigationProbe(IServiceProvider services)
        : IPipelineBehavior<GetOwnCapabilitiesQuery, Result<OwnCapabilitiesDto>>
    {
        public async Task<Result<OwnCapabilitiesDto>> Handle(GetOwnCapabilitiesQuery request,
            RequestHandlerDelegate<Result<OwnCapabilitiesDto>> next, CancellationToken cancellationToken)
        {
            Assert.Equal("forbidden", (await services.GetRequiredService<IOwnTenantNavigationReader>()
                .ReadAsync(null, 20, cancellationToken)).Error!.Code);
            return await next();
        }
    }

    private sealed class RetainedCoverage(OwnTenantNavigationAccess retained) : IOwnTenantNavigationCoverage
    {
        public Task<Result<OwnTenantNavigationAccess>> ReadCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Result.Ok(retained));
    }

    private sealed class AfterFirstCoverage(IOwnTenantNavigationCoverage inner, Func<Task> after) : IOwnTenantNavigationCoverage
    {
        private bool _first = true;
        public async Task<Result<OwnTenantNavigationAccess>> ReadCurrentAsync(CancellationToken cancellationToken = default)
        {
            var result = await inner.ReadCurrentAsync(cancellationToken);
            if (_first && result.IsSuccess) { _first = false; await after(); }
            return result;
        }
    }
}

public sealed class IdentityOwnNavigationFixture : IAsyncLifetime
{
    public TenancyAdministrationFixture Administration { get; } = new();
    public ProductionHostFixture Http { get; } = new();

    public async Task InitializeAsync()
    {
        await Administration.InitializeAsync();
        foreach (var (tenant, organization, host) in new (Guid, Guid?, string)[]
        {
            (SchemaFixture.TenantA, null, "own-navigation-a.example.invalid"),
            (SchemaFixture.TenantA, SchemaFixture.OrgA1, "own-navigation-a1.example.invalid"),
            (SchemaFixture.TenantA, SchemaFixture.OrgA2, "own-navigation-a2.example.invalid"),
            (SchemaFixture.TenantB, null, "own-navigation-b.example.invalid"),
        })
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Administration.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, tenant, organization);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.platform_host_to_tenant(host,tenant_id,organization_id,is_active,is_publicly_live)
                VALUES(@host,@tenant,@organization,true,false)
                """, ("host", host), ("tenant", tenant), ("organization", organization));
            await transaction.CommitAsync();
        }
    }

    internal Task<RoleGrantPipelineHarness> CreateAsync(string correlation, Guid actor, Guid? organization,
        Action<IServiceCollection>? configure = null) => Administration.CreateAsync(correlation, actor, organization, services =>
        {
            services.AddScoped<IOwnTenantNavigationReader, OwnTenantNavigationReader>();
            configure?.Invoke(services);
        });

    internal async Task<string> TenantNameAsync()
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Administration.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<string>(app, transaction, "SELECT display_name FROM public.tenants WHERE id=@tenant", ("tenant", SchemaFixture.TenantA));
    }

    internal async Task SeedPageAsync(Guid[] organizations, string label)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Administration.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await ExecuteAsync(app, transaction, "UPDATE public.tenants SET display_name=@label WHERE id=@tenant",
            ("label", label), ("tenant", SchemaFixture.TenantA));
        await ExecuteAsync(app, transaction, """
            INSERT INTO public.platform_host_to_tenant(host,tenant_id,organization_id,is_active,is_publicly_live)
            VALUES(@host,@tenant,NULL,true,false)
            """, ("host", OwnNavigationWireProof.LongHost(SchemaFixture.TenantA)), ("tenant", SchemaFixture.TenantA));
        foreach (var id in organizations)
        {
            await ScopeAsync(app, transaction, SchemaFixture.TenantA, id);
            await ExecuteAsync(app, transaction, """
                INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
                VALUES(@id,@tenant,@slug,@label,'Active',clock_timestamp(),@actor,0);
                INSERT INTO public.platform_host_to_tenant(host,tenant_id,organization_id,is_active,is_publicly_live)
                VALUES(@host,@tenant,@id,true,false)
                """, ("id", id), ("tenant", SchemaFixture.TenantA), ("slug", "page-budget-" + id.ToString("N")),
                ("label", label), ("actor", AdminA), ("host", OwnNavigationWireProof.LongHost(id)));
        }
        await transaction.CommitAsync();
    }

    internal async Task SetPageLabelsAsync(Guid[] organizations, string label)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Administration.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await ExecuteAsync(app, transaction, """
            UPDATE public.tenants SET display_name=@label WHERE id=@tenant;
            UPDATE public.organizations SET display_name=@label WHERE tenant_id=@tenant AND id=ANY(@ids)
            """, ("label", label), ("tenant", SchemaFixture.TenantA), ("ids", organizations));
        await transaction.CommitAsync();
    }

    internal async Task ClearPageAsync(Guid[] organizations, string originalName)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Administration.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, exclusive: true);
        await ExecuteAsync(app, transaction, """
            DELETE FROM public.platform_host_to_tenant WHERE tenant_id=@tenant AND (organization_id=ANY(@ids) OR host=@host);
            DELETE FROM public.organizations WHERE tenant_id=@tenant AND id=ANY(@ids);
            UPDATE public.tenants SET display_name=@label WHERE id=@tenant
            """, ("tenant", SchemaFixture.TenantA), ("ids", organizations),
                ("host", OwnNavigationWireProof.LongHost(SchemaFixture.TenantA)), ("label", originalName));
        await transaction.CommitAsync();
    }

    public async Task DisposeAsync()
    {
        await Http.DisposeAsync();
        await Administration.DisposeAsync();
    }
}
