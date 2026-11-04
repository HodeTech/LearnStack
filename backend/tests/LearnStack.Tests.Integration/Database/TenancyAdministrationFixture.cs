using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Administration;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Disposable PostgreSQL18, the canonical live operation owner, and actual v2 release adoption.</summary>
public sealed class TenancyAdministrationFixture : IAsyncLifetime
{
    public MembershipAdministrationFixture Membership { get; } = new();
    public string AppConnectionString => Membership.AppConnectionString;
    public string OwnerConnectionString => Membership.OwnerConnectionString;

    public async Task InitializeAsync()
    {
        await Membership.InitializeAsync();
        await using var harness = await CreateAsync("tenancy-admin-fixture-adoption");
        var release = IdentityRoleRelease.Version2;
        var result = await harness.Sender.Send(new AdoptRoleGrantManifestCommand(
            RoleId(SchemaFixture.TenantA, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant), 0, release.Version, release.Fingerprint));
        Assert.True(result.IsSuccess);
    }

    public Task DisposeAsync() => Membership.DisposeAsync();

    internal Task<RoleGrantPipelineHarness> CreateAsync(string correlation, Guid? actor = null, Guid? organization = null,
        Action<IServiceCollection>? configure = null) =>
        RoleGrantPipelineHarness.CreateAsync(AppConnectionString, Authority, TenantId.From(SchemaFixture.TenantA),
            UserId.From(actor ?? AdminA), correlation, organization is { } branch ? OrganizationId.From(branch) : null,
            services =>
            {
                services.AddModuleDbContext<TenancyDbContext>();
                services.AddScoped<ITenancyAdministrationReader, TenancyAdministrationReader>();
                services.AddScoped<ITenantLocaleEligibilityReader, TenantLocaleEligibilityReader>();
                services.AddScoped<ITenantWriteStore, TenantWriteStore>();
                services.AddScoped<IOrganizationWriteStore, OrganizationWriteStore>();
                services.AddScoped<ITenantSettingWriteStore, TenantSettingWriteStore>();
                services.AddScoped<TenancyAdministrationAuthorization>();
                services.AddSingleton<IAuditCatalogSource, TenancyAuditCatalogSource>();
                services.AddMediatR(configuration => configuration.RegisterServicesFromAssembly(typeof(CreateManagedOrganizationCommandHandler).Assembly));
                configure?.Invoke(services);
            });

    public async Task<Result<T>> SendAsync<T>(IRequest<Result<T>> request, string correlation, Guid? actor = null, Guid? organization = null)
    {
        await using var harness = await CreateAsync(correlation, actor, organization);
        return await harness.Sender.Send(request);
    }

    public async Task<long> TenantVersionAsync()
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        return await ScalarAsync<long>(app, transaction, "SELECT row_version FROM tenants WHERE id=@tenant", ("tenant", SchemaFixture.TenantA));
    }

    public Task<string[]> AuditAsync(string correlation, string operation) => Membership.AuditAsync(correlation, operation);
}
