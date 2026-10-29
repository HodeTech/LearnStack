using System.Text.Json;
using LearnStack.Api.Common;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class TenancyOrganizationConflictTests(TenancyAdministrationFixture fixture)
    : IClassFixture<TenancyAdministrationFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_organization_unique_constraints_remain_fixed_storage_faults_and_roll_back(bool rename)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var duplicateName = "Unowned unique baseline " + suffix;
        var baseline = await fixture.SendAsync(new CreateManagedOrganizationCommand(
            "unique-baseline-" + suffix, duplicateName), "organization-unowned-baseline-" + suffix);
        Assert.True(baseline.IsSuccess);
        var target = await fixture.SendAsync(new CreateManagedOrganizationCommand(
            "unique-target-" + suffix, "Unchanged unique target " + suffix), "organization-unowned-target-" + suffix);
        Assert.True(target.IsSuccess);
        var before = await OrganizationSnapshotAsync();
        Assert.Contains(baseline.Value!.OrganizationId.Value.ToString("D"), before, StringComparison.Ordinal);
        Assert.Contains(target.Value!.OrganizationId.Value.ToString("D"), before, StringComparison.Ordinal);
        var correlation = "organization-unowned-refused-" + suffix;
        var operation = rename ? "tenancy.organization.rename" : "tenancy.organization.create";
        var witness = new SaveFailureWitness();
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.OwnerConnectionString);
        // Only this generated canary participates; unrelated fixture branches may
        // legitimately share names. Its fixed prefix plus UUID contains no quotes.
        // This is a real unique index over the already admitted baseline row.
        await ExecuteAsync(owner, null, $"""
            CREATE UNIQUE INDEX ux_test_unowned ON public.organizations(display_name)
            WHERE tenant_id='{SchemaFixture.TenantA:D}'::uuid AND display_name='{duplicateName}'
            """);
        try
        {
            await using var harness = await fixture.CreateAsync(correlation, configure: services =>
                services.AddSingleton<ISaveChangesInterceptor>(witness));
            var result = rename
                ? await harness.Sender.Send(new RenameOrganizationCommand(target.Value!.OrganizationId, target.Value.Version, duplicateName))
                : await harness.Sender.Send(new CreateManagedOrganizationCommand("unique-refused-" + suffix, duplicateName));

            Assert.Equal(1, witness.Failures);
            var provider = Assert.IsType<PostgresException>(Assert.IsType<DbUpdateException>(witness.Failure).InnerException);
            Assert.Equal(PostgresErrorCodes.UniqueViolation, provider.SqlState);
            Assert.Equal("ux_test_unowned", provider.ConstraintName);
            // Every persisted column and row is retained, including no extra
            // root from a refused Create and no changed Rename stamp/version.
            Assert.Equal(before, await OrganizationSnapshotAsync());
            foreach (var row in await fixture.AuditAsync(correlation, operation))
            {
                using var audit = JsonDocument.Parse(row);
                Assert.NotEqual("success", audit.RootElement.GetProperty("outcome").GetString());
            }
            Assert.True(result.IsFailure);
            Assert.Equal("dependency_unavailable", result.Error!.Code);
            var problem = ProblemDetailsFactory.For(result.Error);
            Assert.Equal(503, problem.Status);
            Assert.Equal("dependency_unavailable", problem.Extensions["code"]);
            Assert.False(problem.Extensions.ContainsKey("errors"));
        }
        finally
        {
            await ExecuteAsync(owner, null, "DROP INDEX public.ux_test_unowned");
        }
    }

    private async Task<string> OrganizationSnapshotAsync()
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<string>(app, transaction, """
            SELECT COALESCE(jsonb_agg(to_jsonb(organization) ORDER BY organization.id), '[]'::jsonb)::text
            FROM public.organizations AS organization WHERE organization.tenant_id=@tenant
            """, ("tenant", SchemaFixture.TenantA));
    }

    private sealed class SaveFailureWitness : SaveChangesInterceptor
    {
        public Exception? Failure { get; private set; }
        public int Failures { get; private set; }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is TenancyDbContext)
            {
                Failure = eventData.Exception;
                Failures++;
            }
            return Task.CompletedTask;
        }
    }
}
