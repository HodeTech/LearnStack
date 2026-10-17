using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Tenancy.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData("InterfaceDeclarations")]
    [InlineData("StructDeclarations")]
    [InlineData("GenericDeclarations`1")]
    public async Task Construction_free_manifest_declarations_remain_valid_through_recipient_acquisition(string declaration)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var declared = Fixture.Module.Application.GetType("LearnStack.Modules.JobsRecurrenceFixture.Application." + declaration, true)!;
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false, declared);
        var result = await Store(source).TryClaimRecipientAsync(Fixture.Recurrences(manifest));
        result.ThrowIfFailed();
        result.CommitAcknowledged.Should().BeTrue();
        result.CleanupConfirmed.Should().BeTrue();
        result.Value.Should().NotBeNull("registration accepts construction-free module metadata without requiring a concrete class");
        result.Value!.DeclarationType.Should().Be(declared);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Independent_sources_publish_only_one_acknowledged_recipient_claim_with_the_exact_stored_route(bool organization)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var first = Source(database);
        await using var second = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(first), organization);
        var registry = Fixture.Recurrences(manifest);
        var results = await Task.WhenAll(Store(first).TryClaimRecipientAsync(registry), Store(second).TryClaimRecipientAsync(registry));
        foreach (var result in results) { result.ThrowIfFailed(); result.CleanupConfirmed.Should().BeTrue(); }
        var acquired = results.Where(result => result.Value is not null).Should().ContainSingle().Subject;
        acquired.CommitAcknowledged.Should().BeTrue();
        var claim = acquired.Value!;
        claim.RecipientId.Value.Version.Should().Be(7);
        claim.OccurrenceId.Value.Version.Should().Be(7);
        claim.TenantId.Value.Should().Be(SchemaFixture.TenantA);
        (claim.OrganizationId?.Value).Should().Be(organization ? SchemaFixture.OrgA1 : (Guid?)null);
        claim.RecurrenceKey.Should().Be(manifest.Facts.Key);
        claim.Revision.Should().Be(manifest.Facts.Revision);
        claim.ManifestHash.Should().Be(manifest.ManifestHash);
        claim.DefinitionKey.Should().Be(manifest.Facts.Target);
        claim.DeclarationType.Should().Be(manifest.Registration.DeclaringType);
        claim.RecipientPolicy.Should().Be(manifest.Facts.RecipientPolicy);
        claim.DueAt.Should().Be(claim.ScheduledAt);
        claim.Token.Should().NotBeEmpty();
        claim.OwnershipUntil.Should().BeAfter(claim.DbNow);
        claim.HasLiveLease.Should().BeTrue();
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<bool>(dispatcher, """
            SELECT id=@id AND occurrence_id=@occurrence AND tenant_id=@tenant
                AND organization_id IS NOT DISTINCT FROM @organization AND ownership_token=@token
                AND accepted_job_id IS NULL AND accepted_at IS NULL
            FROM background_job_recipients
            """, ("id", claim.RecipientId.Value), ("occurrence", claim.OccurrenceId.Value), ("tenant", SchemaFixture.TenantA),
            ("organization", (object?)claim.OrganizationId?.Value ?? DBNull.Value), ("token", claim.Token))).Should().BeTrue();
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0,
            "claiming a recipient grants no child creation or business execution by itself");
        var repeated = await Store(second).TryClaimRecipientAsync(registry);
        repeated.ThrowIfFailed();
        repeated.Value.Should().BeNull("an acknowledged unexpired acquisition cannot be issued twice");
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("unknown")]
    [InlineData("conflicting")]
    public async Task Recipient_acquisition_defers_incompatible_deployments_without_creating_child_authority(string mode)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var local = mode == "conflicting" ? Fixture.Manifest(manifest.Facts.ActivatesAt, manifest.Facts.Anchor,
            target: "jobsrecurrencefixture.run.v2") : manifest;
        var definitions = Fixture.Definitions(pendingV1: mode == "pending", omitV1: mode == "unknown");
        var result = await Store(source, definitions).TryClaimRecipientAsync(Fixture.Recurrences(local));
        result.ThrowIfFailed();
        result.Value.Should().BeNull();
        result.CommitAcknowledged.Should().BeTrue();
        result.CleanupConfirmed.Should().BeTrue();
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<bool>(dispatcher, """
            SELECT ownership_token IS NULL AND ownership_until IS NULL AND accepted_job_id IS NULL
                AND skipped_at IS NULL AND quarantined_at IS NULL AND last_error='deployment_mismatch'
                AND available_after>clock_timestamp() FROM background_job_recipients
            """)).Should().BeTrue();
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0);
    }

    [Fact]
    public async Task An_app_locked_recipient_is_skipped_without_publishing_a_claim_or_spending_business_work()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(app, transaction, "SELECT id FROM background_job_recipients FOR UPDATE");
        var result = await Store(source).TryClaimRecipientAsync(Fixture.Recurrences(manifest));
        result.ThrowIfFailed();
        result.Value.Should().BeNull();
        await transaction.RollbackAsync();
        var retry = await Store(source).TryClaimRecipientAsync(Fixture.Recurrences(manifest));
        retry.ThrowIfFailed();
        retry.Value.Should().NotBeNull("releasing the actual competing lock restores claim eligibility");
    }

    private static async Task<BackgroundJobRecurrenceDescriptor> PrepareClaimRouteAsync(DisposableSchemaDatabase database,
        PostgresBackgroundJobControlStore store, bool organization, Type? declarationType = null)
    {
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        var today = await TodayAsync(database);
        var policy = organization ? BackgroundJobRecipientPolicy.ServingOrganizations : BackgroundJobRecipientPolicy.ServingTenants;
        var manifest = Fixture.Manifest(today.AddDays(-2), today.AddDays(-2), policy: policy,
            target: organization ? "jobsrecurrencefixture.organization.v1" : "jobsrecurrencefixture.run.v1", declarationType: declarationType);
        await AssertRegistrationAsync(store, manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
        var occurrence = await store.MaterializeRecurrenceAsync(Fixture.Recurrences(manifest));
        occurrence.ThrowIfFailed();
        occurrence.Value.Should().NotBeNull();
        var page = await store.MaterializeRecipientPageAsync(Fixture.Recurrences(manifest), BackgroundJobLifecycleProjectionDeclaration.Create());
        page.ThrowIfFailed();
        page.Value!.InsertedRecipients.Should().Be(1);
        return manifest;
    }
}
