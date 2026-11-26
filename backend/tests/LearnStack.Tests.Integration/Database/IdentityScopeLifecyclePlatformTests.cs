using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Tenancy's lifecycle port read on an entered platform scope (ADR-0107 § 7): many groups in one statement, in their order,
/// with no announcement, the same four facts the announced read answers, and its refusals before any statement runs.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class IdentityScopeLifecyclePlatformTests(SchemaFixture schema)
{
    private static readonly TenantId TenantA = TenantId.From(SchemaFixture.TenantA);
    private static readonly TenantId TenantB = TenantId.From(SchemaFixture.TenantB);

    [Fact]
    public async Task The_platform_read_answers_every_group_in_order_with_the_announced_read_s_facts()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        await kit.OrganizationStatusAsync(SchemaFixture.TenantA, SchemaFixture.OrgA2, "Archived");
        await kit.TenantStatusAsync(SchemaFixture.TenantB, "Suspended");
        // The platform form reads on the handle it is given and never on the announced unit, so none is constructed.
        var reader = new IdentityScopeLifecycleReader(null!);
        IdentityLifecycleScope[] scopes =
        [
            new(TenantA, null),
            new(TenantA, OrganizationId.From(SchemaFixture.OrgA1)),
            new(TenantA, OrganizationId.From(SchemaFixture.OrgA2)),
            new(TenantA, OrganizationId.From(SchemaFixture.OrgB1)),
            new(TenantA, OrganizationId.From(Guid.CreateVersion7())),
            new(TenantB, null),
            new(TenantB, OrganizationId.From(SchemaFixture.OrgB1)),
            new(TenantId.From(Guid.CreateVersion7()), null),
            new(TenantA, OrganizationId.From(SchemaFixture.OrgA1)),
        ];

        IReadOnlyList<IdentityScopeLifecycle> facts;
        await using (var handle = await kit.Scope().EnterAsync(AccountClosureBeginKit.Admission()))
        {
            facts = await reader.ReadOnPlatformScopeAsync(handle, scopes);
        }

        facts.Should().Equal(
            new IdentityScopeLifecycle(true, true, false, false),
            new IdentityScopeLifecycle(true, true, true, true),
            new IdentityScopeLifecycle(true, true, true, false),
            new IdentityScopeLifecycle(true, true, false, false),
            new IdentityScopeLifecycle(true, true, false, false),
            new IdentityScopeLifecycle(true, false, false, false),
            new IdentityScopeLifecycle(true, false, true, true),
            new IdentityScopeLifecycle(false, false, false, false),
            new IdentityScopeLifecycle(true, true, true, true));

        // The announced read of the same group, on the application role, answers the same.
        await using var source = ApplicationDataSource.Build(kit.Database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using (var frame = await unit.BeginTransactionAsync())
        {
            await unit.SetTenantContextAsync(new Context(TenantB, OrganizationId.From(SchemaFixture.OrgB1)));
            (await new IdentityScopeLifecycleReader(unit).ReadAsync(TenantB, OrganizationId.From(SchemaFixture.OrgB1)))
                .Should().Be(facts[6]);
        }

        // A deleted tenant does not exist for the read, whatever its status.
        await kit.SqlAsync($"SET session_replication_role = replica; UPDATE public.tenants SET deleted_at = now() WHERE id = '{SchemaFixture.TenantB}'");
        await using (var handle = await kit.Scope().EnterAsync(AccountClosureBeginKit.Admission()))
        {
            (await reader.ReadOnPlatformScopeAsync(handle, [new(TenantB, null)])).Should().Equal(new IdentityScopeLifecycle(false, false, false, false));
        }
    }

    [Fact]
    public async Task The_platform_read_refuses_an_empty_an_oversized_or_an_unreal_scope_list_before_any_statement()
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var reader = new IdentityScopeLifecycleReader(null!);
        await using var handle = await kit.Scope().EnterAsync(AccountClosureBeginKit.Admission());

        foreach (var refused in new IReadOnlyList<IdentityLifecycleScope>[]
                 {
                     [],
                     [.. Enumerable.Repeat(new IdentityLifecycleScope(TenantA, null), IIdentityScopeLifecycleReader.MaximumPlatformScopes + 1)],
                     [new(TenantId.PlatformSentinel, null)],
                     [new(TenantId.From(Guid.Empty), null)],
                     [new(TenantA, OrganizationId.From(Guid.Empty))],
                     [null!],
                 })
        {
            var act = () => reader.ReadOnPlatformScopeAsync(handle, refused);
            await act.Should().ThrowAsync<ArgumentException>();
        }

        // Nothing ran, so the scope is still usable: the bound itself is admitted.
        (await reader.ReadOnPlatformScopeAsync(handle,
                [.. Enumerable.Repeat(new IdentityLifecycleScope(TenantA, null), IIdentityScopeLifecycleReader.MaximumPlatformScopes)]))
            .Should().HaveCount(IIdentityScopeLifecycleReader.MaximumPlatformScopes).And.OnlyContain(fact => fact.TenantAllowsOrdinaryWork);
        await FluentActions.Awaiting(() => reader.ReadOnPlatformScopeAsync(null!, [new(TenantA, null)]))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    private sealed record Context(TenantId TenantId, OrganizationId? OrganizationId) : ITenantContext
    {
        public bool IsResolved => true;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public string? CorrelationId => null;
        public string? ModuleName => "identity";
    }
}
