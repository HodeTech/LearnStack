using System.Text.Json;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenancyAdministrationTests(TenancyAdministrationFixture fixture) : IClassFixture<TenancyAdministrationFixture>
{
    [Fact]
    public async Task A_retained_organization_slug_is_an_exact_field_refusal_without_another_root_or_success_audit()
    {
        var slug = "collision-" + Guid.NewGuid().ToString("N");
        var first = await fixture.SendAsync(new CreateManagedOrganizationCommand(slug, "First organization"), "tenancy-slug-first");
        Assert.True(first.IsSuccess);
        var result = await fixture.SendAsync(new CreateManagedOrganizationCommand(slug, "Refused organization"), "tenancy-slug-refused");
        Assert.True(result.IsFailure);
        Assert.Equal("business_rule_violation", result.Error!.Code);
        Assert.NotNull(result.Error.Details);
        Assert.Equal("lockey_slug_taken", Assert.Single(result.Error.Details!["Slug"]).Key);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        Assert.Equal(1L, await ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM organizations WHERE slug=@slug", ("slug", slug)));
        Assert.Equal("First organization", await ScalarAsync<string>(app, transaction,
            "SELECT display_name FROM organizations WHERE slug=@slug", ("slug", slug)));
        Assert.Equal(0L, await ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM audit_log WHERE correlation_id='tenancy-slug-refused' AND outcome='success'"));
    }

    private const string Theme = """{"primary":"#2345aa","background":"#ffffff","foreground":"#111111","muted":"#555555"}""";

    [Fact]
    public async Task Human_create_rename_default_archive_locale_and_theme_share_real_owner_and_audit()
    {
        var created = await fixture.SendAsync(new CreateManagedOrganizationCommand("human-" + Guid.NewGuid().ToString("N"), "Created branch"), "tenancy-human-create");
        Assert.True(created.IsSuccess);
        var id = created.Value!.OrganizationId;
        var renamed = await fixture.SendAsync(new RenameOrganizationCommand(id, created.Value.Version, "Renamed branch"), "tenancy-human-rename");
        Assert.True(renamed.IsSuccess);
        var tenantBefore = await fixture.TenantVersionAsync();
        var selected = await fixture.SendAsync(new SelectDefaultOrganizationCommand(id, tenantBefore), "tenancy-human-select");
        Assert.True(selected.IsSuccess);
        Assert.Equal(tenantBefore + 1, selected.Value);
        var detail = await fixture.SendAsync(new GetOrganizationDetailQuery(id), "tenancy-human-detail");
        Assert.True(detail.IsSuccess);
        Assert.Equal("Renamed branch", detail.Value!.DisplayName);
        Assert.True(detail.Value.IsDefault);
        Assert.Equal(renamed.Value!.Version, detail.Value.Version);
        Assert.Equal("business_rule_violation", (await fixture.SendAsync(new ArchiveOrganizationCommand(id, detail.Value.Version), "tenancy-human-default-archive")).Error!.Code);
        var restored = await fixture.SendAsync(new SelectDefaultOrganizationCommand(OrganizationId.From(SchemaFixture.OrgA1), selected.Value), "tenancy-human-default-restore");
        Assert.True(restored.IsSuccess);
        var archived = await fixture.SendAsync(new ArchiveOrganizationCommand(id, renamed.Value.Version), "tenancy-human-archive");
        Assert.True(archived.IsSuccess);
        var archivedDetail = await fixture.SendAsync(new GetOrganizationDetailQuery(id), "tenancy-human-archived-detail");
        Assert.Equal(ManagedOrganizationStatus.Archived, archivedDetail.Value!.Status);
        Assert.False(archivedDetail.Value.IsDefault);
        Assert.Equal("not_found", (await fixture.SendAsync(new SelectDefaultOrganizationCommand(id, restored.Value), "tenancy-human-archived-default")).Error!.Code);
        var added = await fixture.SendAsync(new AddManagedTenantLocaleCommand(restored.Value, "de", true, false, 9), "tenancy-human-locale-add");
        Assert.True(added.IsSuccess);
        var promoted = await fixture.SendAsync(new SetManagedDefaultTenantLocaleCommand(added.Value, "de"), "tenancy-human-locale-default");
        Assert.True(promoted.IsSuccess);
        var removed = await fixture.SendAsync(new RemoveTenantLocaleCommand(promoted.Value, "tr"), "tenancy-human-locale-remove");
        Assert.True(removed.IsSuccess);
        Assert.Equal("business_rule_violation", (await fixture.SendAsync(new RemoveTenantLocaleCommand(removed.Value, "de"), "tenancy-human-default-locale-remove")).Error!.Code);
        var disabled = await fixture.SendAsync(new SetTenantLocaleEnabledCommand(removed.Value, "en", false), "tenancy-human-locale-disable");
        Assert.True(disabled.IsSuccess);
        var branding = await fixture.SendAsync(new SetManagedTenantBrandingCommand(Theme, null), "tenancy-human-branding");
        Assert.True(branding.IsSuccess);
        Assert.True((await fixture.SendAsync(new SetManagedTenantBrandingCommand(Theme, branding.Value), "tenancy-human-branding-repeat")).IsSuccess);
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        Assert.Equal(0L, await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM tenant_locales WHERE locale='tr'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM tenant_locales WHERE is_default AND is_enabled AND locale='de'"));
        Assert.Equal(1L, await ScalarAsync<long>(app, transaction, "SELECT count(*) FROM organizations WHERE id=@id AND status='Archived'", ("id", id.Value)));
        foreach (var (correlation, operation, entity, entityId) in new[]
        {
            ("tenancy-human-create", "tenancy.organization.create", "Organization", id.Value.ToString("D")),
            ("tenancy-human-select", "tenancy.organization.select_default", "Tenant", SchemaFixture.TenantA.ToString("D")),
            ("tenancy-human-archive", "tenancy.organization.archive", "Organization", id.Value.ToString("D")),
        })
        {
            using var audit = JsonDocument.Parse(Assert.Single(await fixture.AuditAsync(correlation, operation)));
            Assert.Equal("success", audit.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("Must", audit.RootElement.GetProperty("class").GetString());
            Assert.Equal(entity, audit.RootElement.GetProperty("entity").GetString());
            Assert.Equal(entityId, audit.RootElement.GetProperty("id").GetString());
        }
    }

    [Fact]
    public async Task Bounded_directory_cursor_preserves_archived_rows_empty_metadata_and_exact_context()
    {
        var first = await fixture.SendAsync(new ListOrganizationsQuery(null, 1), "tenancy-page-first");
        Assert.True(first.IsSuccess);
        Assert.Single(first.Value!.Items);
        Assert.True(first.Value.PageInfo.HasNext);
        Assert.NotNull(first.Value.PageInfo.NextCursor);
        var seen = first.Value.Items.Select(row => row.OrganizationId).ToHashSet();
        var cursor = first.Value.PageInfo.NextCursor;
        while (cursor is not null)
        {
            var next = await fixture.SendAsync(new ListOrganizationsQuery(cursor, 1), "tenancy-page-next");
            Assert.True(next.IsSuccess);
            Assert.All(next.Value!.Items, row => Assert.True(seen.Add(row.OrganizationId)));
            Assert.Equal(first.Value.DefaultOrganizationId, next.Value.DefaultOrganizationId);
            Assert.Equal(first.Value.TenantVersion, next.Value.TenantVersion);
            Assert.Null(next.Value.PageInfo.PreviousCursor);
            Assert.False(next.Value.PageInfo.HasPrevious);
            cursor = next.Value.PageInfo.NextCursor;
        }
        var wrongActor = await fixture.SendAsync(new ListOrganizationsQuery(first.Value.PageInfo.NextCursor, 1), "tenancy-page-wrong-actor", PersonA);
        Assert.Equal("forbidden", wrongActor.Error!.Code);
        Assert.Equal("validation_failed", (await fixture.SendAsync(new ListOrganizationsQuery("not!base64", 1), "tenancy-page-malformed")).Error!.Code);
        var detail = await fixture.SendAsync(new GetOrganizationDetailQuery(OrganizationId.From(SchemaFixture.OrgB1)), "tenancy-detail-foreign");
        Assert.Equal("not_found", detail.Error!.Code);
        // A canonical contextual cursor at the maximum UUID7 anchor yields no rows but retains Tenant metadata.
        await using var harness = await fixture.CreateAsync("tenancy-page-empty-owner");
        await using var frame = await harness.Unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
        var context = harness.Services.GetRequiredService<ITenantContext>();
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new object?[]
        {
            1, "tenancy.organization.list", context.TenantId.Value.ToString("D"), context.UserId!.Value.Value.ToString("D"), null, "organization_id:asc",
        });
        var scope = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { v = 1, scope, id = "ffffffff-ffff-7fff-bfff-ffffffffffff" });
        var emptyCursor = Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var empty = await harness.Services.GetRequiredService<ITenancyAdministrationReader>().ListOrganizationsAsync(emptyCursor, 1);
        Assert.NotNull(empty);
        Assert.Empty(empty.Items);
        Assert.Equal(first.Value.DefaultOrganizationId, empty.DefaultOrganizationId);
        Assert.Equal(first.Value.TenantVersion, empty.TenantVersion);
        Assert.False(empty.PageInfo.HasNext);
        Assert.Null(empty.PageInfo.PreviousCursor);
        Assert.False(empty.PageInfo.HasPrevious);
        Assert.Empty(harness.Services.GetRequiredService<TenancyDbContext>().ChangeTracker.Entries());
        await frame.FailAsync();
    }

    [Fact]
    public async Task An_authorized_actor_or_branch_change_refuses_another_contexts_cursor()
    {
        var first = await fixture.SendAsync(new ListOrganizationsQuery(null, 1), "tenancy-cursor-context-source");
        Assert.True(first.IsSuccess);
        Assert.NotNull(first.Value!.PageInfo.NextCursor);
        var cursor = first.Value.PageInfo.NextCursor;

        // Tenant authority remains eligible in a branch. This fresh success proves
        // the following refusal reaches cursor validation rather than capability denial.
        var branch = await fixture.SendAsync(new ListOrganizationsQuery(null, 1),
            "tenancy-cursor-branch-positive", organization: SchemaFixture.OrgA1);
        Assert.True(branch.IsSuccess);
        Assert.NotEmpty(branch.Value!.Items);
        var wrongBranch = await fixture.SendAsync(new ListOrganizationsQuery(cursor, 1),
            "tenancy-cursor-branch-refusal", organization: SchemaFixture.OrgA1);
        Assert.Equal("validation_failed", wrongBranch.Error!.Code);
        Assert.Equal("lockey_invalid_value", Assert.Single(wrongBranch.Error.Details!["Cursor"]).Key);

        var other = await fixture.Membership.AddMemberAsync(role: BuiltInRoleKey.TenantAdmin);
        var actor = await fixture.SendAsync(new ListOrganizationsQuery(null, 1),
            "tenancy-cursor-actor-positive", other.Actor);
        Assert.True(actor.IsSuccess);
        Assert.NotEmpty(actor.Value!.Items);
        var wrongActor = await fixture.SendAsync(new ListOrganizationsQuery(cursor, 1),
            "tenancy-cursor-actor-refusal", other.Actor);
        Assert.Equal("validation_failed", wrongActor.Error!.Code);
        Assert.Equal("lockey_invalid_value", Assert.Single(wrongActor.Error.Details!["Cursor"]).Key);
    }

    [Fact]
    public async Task Direct_handler_without_invocation_and_branch_writes_refuse_before_any_tenancy_lookup()
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        await ExecuteAsync(owner, null, """
            CREATE FUNCTION tenancy_direct_lookup_fault() RETURNS boolean LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'unexpected tenancy lookup' USING ERRCODE='P0399'; END $$;
            REVOKE ALL ON FUNCTION tenancy_direct_lookup_fault() FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION tenancy_direct_lookup_fault() TO learnstack_app;
            CREATE POLICY tenancy_direct_lookup_fault ON tenants AS RESTRICTIVE FOR SELECT TO learnstack_app USING(tenancy_direct_lookup_fault());
            """);
        try
        {
            await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString))
            {
                await using var transaction = await app.BeginTransactionAsync();
                await ScopeAsync(app, transaction, SchemaFixture.TenantA);
                Assert.Equal("P0399", (await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync<Guid>(app, transaction, "SELECT id FROM tenants"))).SqlState);
            }
            await using var harness = await fixture.CreateAsync("tenancy-direct-handler");
            await using var frame = await harness.Unit.BeginTransactionAsync();
            await harness.Unit.SetTenantContextAsync(harness.Services.GetRequiredService<ITenantContext>());
            var handler = harness.Services.GetRequiredService<IRequestHandler<CreateManagedOrganizationCommand, Result<OrganizationMutationResult>>>();
            Assert.Equal("forbidden", (await handler.Handle(new("direct-forbidden", "Must not write"), CancellationToken.None)).Error!.Code);
            await frame.FailAsync();
        }
        finally { await ExecuteAsync(owner, null, "DROP POLICY tenancy_direct_lookup_fault ON tenants; DROP FUNCTION tenancy_direct_lookup_fault()"); }
        Assert.Equal("forbidden", (await fixture.SendAsync(new CreateManagedOrganizationCommand("learner-forbidden", "Must not write"), "tenancy-no-admin", PersonA)).Error!.Code);
        Assert.Equal("forbidden", (await fixture.SendAsync(new CreateManagedOrganizationCommand("branch-forbidden", "Must not write"), "tenancy-branch-write", organization: SchemaFixture.OrgA1)).Error!.Code);
    }
}
