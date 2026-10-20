using FluentAssertions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
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
