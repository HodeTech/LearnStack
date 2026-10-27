using System.Data.Common;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class GlobalIdentityUnitOfWorkTests(SchemaFixture schema)
{
    private static readonly GlobalIdentitySubject Subject = new(
        IdentityAuthorityId.From(Guid.Parse("019a1929-5450-7bbb-8df1-516e8e002501")),
        "https://identity.example/realms/learnstack", "unit-owner-proof-subject");

    [Fact]
    public async Task Read_only_global_owner_announces_only_self_authority_and_clears_unrelated_session_values()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        foreach (var name in new[] { "app.tenant_id", "app.organization_id", "app.scope", "app.resolving_host" })
            await ScalarAsync(unit, "SELECT set_config(@name, 'stale-session-value', false)", ("name", name));
        var descriptor = ProofMint.Lookup(Subject);
        await using var frame = await unit.BeginGlobalIdentityTransactionAsync(descriptor, TransactionMode.ReadOnly);
        frame.IsOwner.Should().BeTrue();
        (await ScalarAsync(unit, "SHOW transaction_isolation")).Should().Be("read committed");
        (await ScalarAsync(unit, "SHOW transaction_read_only")).Should().Be("on");
        foreach (var name in new[] { "app.tenant_id", "app.organization_id", "app.scope", "app.resolving_host" })
            (await ScalarAsync(unit, "SELECT current_setting(@name, true)", ("name", name))).Should().BeEmpty();
        (await ScalarAsync(unit, "SELECT current_setting('app.identity_authority_id', true)"))
            .Should().Be(Subject.AuthorityId.Value.ToString("D"));
        (await ScalarAsync(unit, "SELECT current_setting('app.identity_subject_key', true)"))
            .Should().Be(Subject.SubjectKey.Hex);
        unit.IsTenantContextIssuedOn(unit.Transaction).Should().BeFalse();
        unit.GetTenantContextAnnouncement(unit.Transaction).Should().BeNull();
        unit.GetGlobalIdentityAnnouncement(unit.Transaction)!.Descriptor.Should().BeSameAs(descriptor);
        await frame.CompleteAsync();
        unit.IsGlobalIdentityTransaction.Should().BeFalse();
        unit.GetGlobalIdentityAnnouncement(unit.Transaction).Should().BeNull();
    }

    [Fact]
    public async Task Only_the_exact_enrolled_context_can_issue_commands_and_validation_seals_it_before_commit()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        var descriptor = ProofMint.Creation(Subject);
        await using var frame = await unit.BeginGlobalIdentityTransactionAsync(descriptor, TransactionMode.ReadWrite);
        await using var context = Context(unit);
        var beforeLock = () => unit.EnrollGlobalIdentityContext(context, descriptor);
        beforeLock.Should().Throw<InvalidOperationException>();
        await unit.AcquireGlobalIdentitySubjectLockAsync(descriptor);
        unit.EnrollGlobalIdentityContext(context, descriptor);
        await using var foreign = Context(unit);
        var replace = () => unit.EnrollGlobalIdentityContext(foreign, descriptor);
        replace.Should().Throw<InvalidOperationException>();
        (await context.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync()).Should().Be(1);
        await FluentActions.Awaiting(() => foreign.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync())
            .Should().ThrowAsync<TenantContextMissingException>()
            .WithMessage("The command does not belong to the enrolled global identity context.");
        await unit.ValidateGlobalIdentityConstraintsAsync(descriptor);
        unit.IsGlobalIdentityContextEnrolled(context, unit.Transaction).Should().BeFalse();
        await FluentActions.Awaiting(() => context.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync())
            .Should().ThrowAsync<TenantContextMissingException>()
            .WithMessage("The command does not belong to the enrolled global identity context.");
        await frame.CompleteAsync();
        unit.IsRollbackOnly.Should().BeFalse();
    }

    [Theory]
    [InlineData("begin")]
    [InlineData("tenant")]
    [InlineData("provision")]
    public async Task Ordinary_entries_cannot_join_or_retarget_a_global_owner(string entry)
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        await using var frame = await unit.BeginGlobalIdentityTransactionAsync(ProofMint.Creation(Subject), TransactionMode.ReadWrite);
        Func<Task> invalid = entry switch
        {
            "tenant" => () => unit.SetTenantContextAsync(UnresolvedTenantContext.Instance),
            "provision" => () => unit.SetProvisioningTenantContextAsync(TenantId.From(SchemaFixture.TenantA)),
            _ => async () => { await unit.BeginTransactionAsync(); }
            ,
        };
        await invalid.Should().ThrowAsync<InvalidOperationException>();
        unit.IsRollbackOnly.Should().BeTrue();
        unit.IsTenantContextIssuedOn(unit.Transaction).Should().BeFalse();
    }

    [Fact]
    public async Task Global_entry_cannot_join_an_ordinary_transaction()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetProvisioningTenantContextAsync(TenantId.From(SchemaFixture.TenantA));
        await FluentActions.Awaiting(() => unit.BeginGlobalIdentityTransactionAsync(ProofMint.Lookup(Subject), TransactionMode.ReadOnly))
            .Should().ThrowAsync<InvalidOperationException>();
        unit.IsRollbackOnly.Should().BeTrue();
        unit.GetGlobalIdentityAnnouncement(unit.Transaction).Should().BeNull();
    }

    [Fact]
    public async Task A_creation_owner_without_final_constraint_validation_rolls_back_before_physical_commit()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        await using var frame = await unit.BeginGlobalIdentityTransactionAsync(ProofMint.Creation(Subject), TransactionMode.ReadWrite);
        await FluentActions.Awaiting(() => frame.CompleteAsync()).Should().ThrowAsync<InvalidOperationException>();
        unit.HasActiveTransaction.Should().BeFalse();
        unit.IsRollbackOnly.Should().BeTrue();
        unit.GetGlobalIdentityAnnouncement(unit.Transaction).Should().BeNull();
    }

    [Fact]
    public async Task A_later_generation_has_no_stale_global_enrollment_or_identity_session_authority()
    {
        await using var source = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        var first = await unit.BeginGlobalIdentityTransactionAsync(ProofMint.Lookup(Subject), TransactionMode.ReadOnly);
        var old = unit.GetGlobalIdentityAnnouncement(unit.Transaction)!;
        var oldTransaction = unit.Transaction;
        await using var context = Context(unit);
        unit.EnrollGlobalIdentityContext(context, old.Descriptor);
        await first.CompleteAsync();
        await using var later = await unit.BeginTransactionAsync();
        await unit.SetProvisioningTenantContextAsync(TenantId.From(SchemaFixture.TenantA));
        unit.GetTenantContextAnnouncement(unit.Transaction)!.Generation.Should().BeGreaterThan(old.Generation);
        unit.GetGlobalIdentityAnnouncement(oldTransaction).Should().BeNull();
        unit.IsGlobalIdentityContextEnrolled(context, unit.Transaction).Should().BeFalse();
        (await ScalarAsync(unit, "SELECT current_setting('app.identity_authority_id', true)")).Should().BeEmpty();
        (await ScalarAsync(unit, "SELECT current_setting('app.identity_subject_key', true)")).Should().BeEmpty();
        await first.DisposeAsync();
        unit.HasActiveTransaction.Should().BeTrue();
    }

    private static NpgsqlUnitOfWork Unit(NpgsqlDataSource source) => new(source, NullLogger<NpgsqlUnitOfWork>.Instance);

    private static ProofContext Context(NpgsqlUnitOfWork unit)
    {
        var context = new ProofContext(new DbContextOptionsBuilder<ProofContext>()
            .UseNpgsql(unit.Connection).AddInterceptors(new TenantContextGuardInterceptor(unit)).Options);
        context.Database.UseTransaction(unit.Transaction);
        return context;
    }

    private static async Task<string> ScalarAsync(NpgsqlUnitOfWork unit, string sql, params (string Key, string Value)[] values)
    {
        await using var command = unit.Connection.CreateCommand();
        command.Transaction = unit.Transaction;
        command.CommandText = sql;
        foreach (var value in values)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = value.Key;
            parameter.Value = value.Value;
            command.Parameters.Add(parameter);
        }
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    private sealed class ProofContext(DbContextOptions<ProofContext> options) : DbContext(options);
    private sealed class ProofEntity;
    private sealed class ProofMint : GlobalIdentityOwnerDescriptor.MintingSource
    {
        public static GlobalIdentityOwnerDescriptor Lookup(GlobalIdentitySubject subject) => ForLookup(subject, typeof(ProofContext), typeof(ProofEntity));
        public static GlobalIdentityOwnerDescriptor Creation(GlobalIdentitySubject subject) => ForBootstrap(subject, typeof(ProofContext), typeof(ProofEntity));
    }
}
