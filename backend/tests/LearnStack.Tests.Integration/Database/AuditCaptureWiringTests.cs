using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using LearnStack.Api.Composition;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// That the capture interceptor is actually attached to a module <c>DbContext</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The one thing the unit suite cannot say.</b> Those cases call
/// <c>AuditChangeTrackerInterceptor.Capture</c> directly, so they constrain what it does
/// and say nothing about whether anything ever calls it. Both EF hooks and the
/// <c>AddInterceptors</c> line in <c>ModuleDbContextRegistration</c> could be deleted with
/// every one of them green — and the result would be audit rows with empty snapshots and
/// no error anywhere, which is the failure this whole seam exists to prevent.
/// </para>
/// <para>
/// It goes through the <b>real</b> registration: <c>AddModuleDbContext</c>, the real
/// <c>NpgsqlUnitOfWork</c>, and the interceptor registered as
/// <c>ISaveChangesInterceptor</c> exactly as both composition roots register it. A
/// hand-built <c>DbContextOptions</c> would test the arrangement this case exists to
/// doubt.
/// </para>
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuditCaptureWiringTests
{
    private readonly SchemaFixture _schema;

    public AuditCaptureWiringTests(SchemaFixture schema) => _schema = schema;

    [Fact]
    public async Task Saving_through_a_module_context_fills_the_request_capture()
    {
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var capture = scope.ServiceProvider.GetRequiredService<IAuditStateCapture>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await using var frame = await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(
            scope.ServiceProvider.GetRequiredService<ITenantContext>());

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        capture.Changes.Should().BeEmpty("nothing has been saved yet");

        var tenant = Tenant.Create(
            TenantId.From(WiringTenant),
            "wiring-probe",
            "Wiring Probe",
            new SystemClock(),
            UserId.From(SchemaFixture.Actor));

        context.Add(tenant);
        await context.SaveChangesAsync();

        var change = capture.Changes.Should().ContainSingle(
            "the interceptor is attached to every module context by AddModuleDbContext").Subject;

        change.EntityType.Should().Be(nameof(Tenant));
        change.EntityId.Should().Be(WiringTenant.ToString());
        change.BeforeJson.Should().BeNull("the tenant is being inserted");
        change.AfterJson.Should().Contain("wiring-probe");

        // The bookkeeping the row already carries is not in the snapshot, and the record
        // still is. The three paths asserted here are ones that CAN appear on this entity:
        // Tenant derives from AuditableEntity<TId>, so CreatedAt, UpdatedAt and Version are
        // real mapped properties. An earlier version of this assertion named
        // /Tenant/TenantId and /Tenant/RowVersion — neither of which exists, because Tenant
        // is self-keyed and the concurrency token's PROPERTY is Version — so it asserted
        // the absence of three paths nothing could have produced.
        var paths = change.Fields.Select(field => field.Path).ToList();

        paths.Should().NotContain("/Tenant/CreatedAt");
        paths.Should().NotContain("/Tenant/UpdatedAt");
        paths.Should().NotContain("/Tenant/Version",
            "the concurrency token moves on every write and buries what changed");
        paths.Should().Contain("/Tenant/Slug");
        paths.Should().Contain("/Tenant/CreatedBy", "who did it is the record, not bookkeeping");

        // Rolled back rather than committed: the shared fixture's counts are asserted by
        // the cases in this collection, and a probe tenant would move them.
        await frame.DisposeAsync();
    }

    [Fact]
    public async Task One_scope_shares_one_capture_across_every_module_context()
    {
        // The buffer is per REQUEST, not per context. Two contexts resolving two buffers
        // would give a command that saves through both — which is what a cross-module read
        // model does — two half-snapshots and one row built from whichever was asked.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var asInterface = scope.ServiceProvider.GetRequiredService<IAuditStateCapture>();
        var asConcrete = scope.ServiceProvider.GetRequiredService<AuditStateCapture>();

        asInterface.Should().BeSameAs(asConcrete,
            "the interface registration forwards to the concrete one rather than "
            + "constructing a second buffer");

        await using var other = provider.CreateAsyncScope();

        other.ServiceProvider.GetRequiredService<IAuditStateCapture>()
            .Should().NotBeSameAs(asInterface, "a second request gets a second buffer");
    }

    [Fact]
    public async Task The_synchronous_save_path_fills_the_capture_too()
    {
        // The interceptor overrides BOTH hooks and only the async one was covered: the
        // synchronous SavingChanges body could be emptied with the whole suite green.
        // Nothing in the request path calls SaveChanges today — but the seeder, a data
        // migration and any future job may, and the failure would be silent, which is the
        // one thing this seam exists to prevent.
        await using var provider = BuildProvider();
        await using var scope = provider.CreateAsyncScope();

        var capture = scope.ServiceProvider.GetRequiredService<IAuditStateCapture>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await using var frame = await unitOfWork.BeginTransactionAsync();
        await unitOfWork.SetTenantContextAsync(
            scope.ServiceProvider.GetRequiredService<ITenantContext>());

        var context = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        context.Add(Tenant.Create(
            TenantId.From(WiringTenant),
            "sync-probe",
            "Sync Probe",
            new SystemClock(),
            UserId.From(SchemaFixture.Actor)));

        // Deliberately NOT SaveChangesAsync.
        context.SaveChanges();

        capture.Changes.Should().ContainSingle(
            "the synchronous hook captures exactly as the asynchronous one does");

        await frame.DisposeAsync();
    }

    [Fact]
    public async Task The_real_composition_root_keeps_the_audit_interceptor_behind_another_one()
    {
        // The reason the registration is TryAddEnumerable and not TryAddScoped, asserted
        // against the REAL AddLearnStackPersistence rather than a copy of its lines. Under
        // TryAddScoped a second ISaveChangesInterceptor registered first makes the audit
        // one silently absent, and every audit row then ships with empty snapshots and no
        // error anywhere.
        var services = new ServiceCollection();

        // Registered FIRST, which is the whole point: TryAddScoped skips when any
        // registration of the service type exists.
        services.AddScoped<ISaveChangesInterceptor, HarmlessInterceptor>();

        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor>(
            new StaticTenantContextAccessor(new WiringContext()));
        services.AddTransient<ITenantContext>(sp =>
            sp.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);

        services.AddLearnStackPersistence(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _schema.Postgres.AppConnectionString,
            })
            .Build());

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        scope.ServiceProvider.GetServices<ISaveChangesInterceptor>()
            .Should().ContainSingle(interceptor => interceptor is AuditChangeTrackerInterceptor,
                "the audit capture survives a second interceptor registered ahead of it");
    }

    /// <summary>An interceptor that does nothing, registered only to get in the way.</summary>
    private sealed class HarmlessInterceptor : SaveChangesInterceptor;

    private static readonly Guid WiringTenant = Guid.Parse("cccccccc-9999-7999-8999-999999999999");

    private ServiceProvider BuildProvider()
    {
        // The composition root's shape, with the audit registrations exactly as both roots
        // write them.
        var services = new ServiceCollection();

        services.AddSingleton(NpgsqlDataSource.Create(_schema.Postgres.AppConnectionString));
        services.AddLogging();
        services.AddSingleton<ITenantContextAccessor>(
            new StaticTenantContextAccessor(new WiringContext()));
        services.AddTransient<ITenantContext>(sp =>
            sp.GetRequiredService<ITenantContextAccessor>().Current
            ?? UnresolvedTenantContext.Instance);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();

        services.TryAddScoped<AuditStateCapture>();
        services.TryAddScoped<IAuditStateCapture>(
            sp => sp.GetRequiredService<AuditStateCapture>());
        services.TryAddEnumerable(
            ServiceDescriptor.Scoped<ISaveChangesInterceptor, AuditChangeTrackerInterceptor>());

        services.AddModuleDbContext<TenancyDbContext>();

        return services.BuildServiceProvider();
    }

    private sealed class WiringContext : ITenantContext
    {
        public bool IsResolved => true;

        public TenantId TenantId => LearnStack.SharedKernel.Identifiers.TenantId.From(WiringTenant);

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "tenancy";
    }
}
