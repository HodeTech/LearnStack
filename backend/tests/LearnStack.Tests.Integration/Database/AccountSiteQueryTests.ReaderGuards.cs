using FluentAssertions;
using System.Reflection;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AccountSiteQueryTests
{
    [Fact]
    public async Task Real_configuration_reader_refuses_an_announced_enlisted_writable_frame_with_read_only_control()
    {
        var context = Context(SeedData.English, false);
        await using var host = CreateHost(context);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var owner = await unit.BeginTransactionAsync(TransactionMode.ReadWrite);
            await unit.SetTenantContextAsync(context);
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
            unit.IsTenantContextIssuedOn(unit.Transaction).Should().BeTrue();
            db.Database.CurrentTransaction!.GetDbTransaction().Should().BeSameAs(unit.Transaction);
            (await db.Tenants.AsNoTracking().SingleAsync(row => row.Id == context.TenantId)).DisplayName
                .Should().Be(SeedData.English.DisplayName, "the writable frame can read the same real row");

            var read = async () => await RealReader(db, context, unit).ReadAsync(CancellationToken.None);
            await read.Should().ThrowAsync<TenantContextMissingException>()
                .WithMessage("Public configuration requires matching host provenance and an announced, enlisted read-only transaction.");
            await owner.FailAsync();
            unit.IsRollbackOnly.Should().BeTrue("a failed owner is never reusable for the positive control");
        }
        await using var correctedScope = host.Services.CreateAsyncScope();
        var correctedUnit = correctedScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var readOnly = await correctedUnit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await correctedUnit.SetTenantContextAsync(context);
        var corrected = correctedScope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await AssertRealReaderSuccess(RealReader(corrected, context, correctedUnit), correctedUnit);
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("sentinel")]
    [InlineData("host-absent")]
    [InlineData("host-tenant")]
    [InlineData("host-organization")]
    public async Task Real_configuration_reader_independently_refuses_invalid_host_provenance_with_same_frame_control(string fault)
    {
        var context = Context(SeedData.English, false);
        await using var host = CreateHost(context);
        await using var scope = host.Services.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var owner = await unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await unit.SetTenantContextAsync(context);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await AssertRealReaderSuccess(RealReader(db, context, unit), unit);
        var invalid = fault switch
        {
            "unresolved" => new ReaderGuardContext(context) { IsResolved = false },
            "sentinel" => new ReaderGuardContext(context)
            {
                TenantId = TenantId.PlatformSentinel,
                HostScope = ForgedReaderHostScope(TenantId.PlatformSentinel, null),
            },
            "host-absent" => new ReaderGuardContext(context) { HostScope = null },
            "host-tenant" => new ReaderGuardContext(context) { HostScope = ForgedReaderHostScope(SeedData.Yoga.TenantId, null) },
            "host-organization" => new ReaderGuardContext(context)
            {
                HostScope = ForgedReaderHostScope(context.TenantId, SeedData.English.DefaultOrganization.OrganizationId),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(fault)),
        };

        var read = async () => await RealReader(db, invalid, unit).ReadAsync(CancellationToken.None);
        await read.Should().ThrowAsync<TenantContextMissingException>()
            .WithMessage("Public configuration requires matching host provenance and an announced, enlisted read-only transaction.");

        await AssertRealReaderSuccess(RealReader(db, context, unit), unit);
    }

    [Fact]
    public async Task Real_configuration_reader_refuses_unannounced_transaction_then_reads_after_announcement()
    {
        var context = Context(SeedData.English, false);
        await using var host = CreateHost(context);
        await using var scope = host.Services.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var owner = await unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        db.Database.CurrentTransaction!.GetDbTransaction().Should().BeSameAs(unit.Transaction);
        unit.IsTenantContextIssuedOn(unit.Transaction).Should().BeFalse();
        var reader = RealReader(db, context, unit);

        var read = async () => await reader.ReadAsync(CancellationToken.None);
        await read.Should().ThrowAsync<TenantContextMissingException>()
            .WithMessage("Public configuration requires matching host provenance and an announced, enlisted read-only transaction.");

        await unit.SetTenantContextAsync(context);
        unit.IsTenantContextIssuedOn(unit.Transaction).Should().BeTrue();
        await AssertRealReaderSuccess(reader, unit);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_configuration_reader_refuses_missing_or_foreign_EF_transaction_with_same_role_controls(bool foreign)
    {
        var context = Context(SeedData.English, false);
        await using var host = CreateHost(context);
        await using var scope = host.Services.CreateAsyncScope();
        await using var foreignScope = host.Services.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var other = foreignScope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var owner = await unit.BeginTransactionAsync(TransactionMode.ReadOnly);
        await unit.SetTenantContextAsync(context);
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
        await using var otherOwner = foreign ? await other.BeginTransactionAsync(TransactionMode.ReadOnly) : null;
        if (foreign) await other.SetTenantContextAsync(context);
        var selected = foreign ? other : unit;
        await using var db = new TenancyDbContext(
            new DbContextOptionsBuilder<TenancyDbContext>().UseNpgsql(selected.Connection).Options,
            new StaticTenantContextAccessor(context));
        if (foreign)
        {
            await db.Database.UseTransactionAsync(other.Transaction);
            db.Database.CurrentTransaction!.GetDbTransaction().Should().NotBeSameAs(unit.Transaction);
        }
        else db.Database.CurrentTransaction.Should().BeNull();
        var reader = RealReader(db, context, unit);

        var read = async () => await reader.ReadAsync(CancellationToken.None);
        await read.Should().ThrowAsync<TenantContextMissingException>()
            .WithMessage("Public configuration requires matching host provenance and an announced, enlisted read-only transaction.");

        // Correct the exact missing/mismatched association, retaining the same
        // real EF context, seeded rows and learnstack_app connection.
        if (!foreign) await db.Database.UseTransactionAsync(unit.Transaction);
        await AssertRealReaderSuccess(RealReader(db, context, selected), selected);
    }

    private static PublicTenantConfigurationReader RealReader(TenancyDbContext db, ITenantContext context, IUnitOfWork unit) =>
        new(db, context, unit, NullLogger<PublicTenantConfigurationReader>.Instance);

    // The factory correctly refuses a sentinel host. Reflection plants the corrupt
    // provenance only at this reader boundary so its sentinel clause is independent
    // of the host-mismatch clause and the already announced legitimate transaction.
    private static HostScope ForgedReaderHostScope(TenantId tenant, OrganizationId? organization) =>
        (HostScope)typeof(HostScope).GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, types: [typeof(TenantId), typeof(OrganizationId?)], modifiers: null)!.Invoke([tenant, organization]);

    private sealed record ReaderGuardContext(ITenantContext Actual) : ITenantContext
    {
        public bool IsResolved { get; init; } = Actual.IsResolved;
        public TenantId TenantId { get; init; } = Actual.TenantId;
        public OrganizationId? OrganizationId => Actual.OrganizationId;
        public UserId? UserId => Actual.UserId;
        public TenantContextOrigin? Origin => Actual.Origin;
        public HostScope? HostScope { get; init; } = Actual.HostScope;
        public string? CorrelationId => Actual.CorrelationId;
        public string? ModuleName => Actual.ModuleName;
    }

    private static async Task AssertRealReaderSuccess(PublicTenantConfigurationReader reader, IUnitOfWork unit)
    {
        unit.Mode.Should().Be(TransactionMode.ReadOnly);
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
        var result = await reader.ReadAsync(CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        result.Value!.DisplayName.Should().Be(SeedData.English.DisplayName);
        result.Value.EnabledLocales.Should().Equal("en");
    }
}
