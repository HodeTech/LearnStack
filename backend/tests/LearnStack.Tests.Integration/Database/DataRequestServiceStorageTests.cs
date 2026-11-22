using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The request API's storage seams as <c>learnstack_app</c> on the seeded data-request rows: the explicit group
/// predicate that the canonical row policy alone does not supply, the one-active index mapped to a refusal rather
/// than an exception, and the exact Membership snapshot the creation guard demands.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class DataRequestServiceStorageTests(SchemaFixture fixture)
{
    [Fact]
    public async Task Reads_name_exactly_the_announced_group_although_the_row_policy_shows_an_organization_the_tenant_wide_rows()
    {
        await using var database = await OpenDatabaseAsync();
        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, SchemaFixture.OrgA1))
        {
            // The policy alone admits the tenant-wide group's row into an organization context.
            (await ScalarAsync<long>(scope.Connection, scope.Transaction, "SELECT count(*) FROM public.data_subject_requests WHERE organization_id IS NULL"))
                .Should().Be(1, "the canonical row policy shows an organization context the tenant-wide rows");
            var reader = new DataRequestReader(scope.Db, scope.Unit, scope.Context);
            (await reader.ListAsync(null, null, 101, CancellationToken.None)).Select(request => request.Id.Value)
                .Should().Equal(BranchA1.Request);
            (await reader.ListAsync(UserId.From(PersonA), null, 101, CancellationToken.None)).Select(request => request.Id.Value)
                .Should().Equal(BranchA1.Request);
            (await reader.FindAsync(DataSubjectRequestId.From(TenantWideA.Request), CancellationToken.None)).Should().BeNull();
            (await reader.ReadArtifactsAsync([DataSubjectRequestId.From(TenantWideA.Request)], CancellationToken.None)).Should().BeEmpty();
            (await reader.FindArtifactLockedAsync(DataSubjectRequestId.From(TenantWideA.Request), CancellationToken.None)).Should().BeNull();
            (await Store(scope).ReadSubjectMembershipsAsync(UserId.From(PersonA), CancellationToken.None)).Select(member => member.Value)
                .Should().Equal(BranchMembershipA1);
            (await reader.FindMembershipSubjectAsync(MembershipId.From(LearnerMembershipA), CancellationToken.None)).Should().BeNull(
                "another group's Membership is absent");
            await scope.Frame.FailAsync();
        }

        await using (var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantA, null))
        {
            var reader = new DataRequestReader(scope.Db, scope.Unit, scope.Context);
            (await reader.ListAsync(null, null, 101, CancellationToken.None)).Select(request => request.Id.Value)
                .Should().Equal(TenantWideA.Request);
            var artifacts = await reader.ReadArtifactsAsync([DataSubjectRequestId.From(TenantWideA.Request)], CancellationToken.None);
            artifacts.Should().ContainSingle().Which.Value.Readable.Should().BeTrue();
            (await reader.FindMembershipSubjectAsync(MembershipId.From(LearnerMembershipA), CancellationToken.None))
                .Should().Be(UserId.From(PersonA));
            await scope.Frame.FailAsync();
        }
    }

    [Fact]
    public async Task The_one_active_index_refuses_a_second_active_export_as_a_mapped_refusal_that_leaves_the_transaction_rollback_only()
    {
        await using var database = await OpenDatabaseAsync();
        await using var scope = await DataExportTestKit.OpenAsync(database.AppConnectionString, SchemaFixture.TenantB, SchemaFixture.OrgB1,
            [PersonB]);
        var store = Store(scope);
        var active = await store.FindActiveLockedAsync(UserId.From(PersonB), DataRequestKind.Export, CancellationToken.None);
        active!.Id.Value.Should().Be(BranchB.Request, "the seeded requested export holds the slot");
        // Bypass the eligibility judgment on purpose: the index is the backstop a race meets.
        var second = DataSubjectRequest.FileForSelf(DataSubjectRequestId.From(Guid.CreateVersion7()), DataRequestKind.Export, UserId.From(PersonB),
            new DataRequestScopeGroup(scope.Context.TenantId, scope.Context.OrganizationId), [MembershipId.From(BranchMembershipB)],
            DateTimeOffset.UtcNow).Value!;
        (await store.TryAddAsync(second, CancellationToken.None)).Should().BeFalse();
        scope.Unit.IsRollbackOnly.Should().BeTrue();
        scope.Db.ChangeTracker.Entries<DataSubjectRequest>().Should().NotContain(entry => entry.Entity == second);
    }

    private static DataRequestStore Store(DataExportTestKit.GroupScope scope) =>
        new(scope.Db, scope.Unit, new ProducerExecution(scope.Unit), scope.Context);

    private async Task<DisposableSchemaDatabase> OpenDatabaseAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        return database;
    }
}
