using FluentAssertions;
using FluentAssertions.Execution;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobPositiveSdkTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_typed_provider_method_rechecks_the_acknowledged_Arm_window_after_a_held_constructor(bool expire)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        await using var provider = await BackgroundJobProviderJournalDatabase.CreateAsync(schema.Postgres);
        var resource = Guid.CreateVersion7();
        var prepared = OrganizationId.From(Guid.CreateVersion7());
        var finalized = OrganizationId.From(Guid.CreateVersion7());
        var construction = new BackgroundJobPositiveProviderConstructionProbe(hold: true);
        await using var host = Fixture.Host(database, provider.Settings, construction);
        var flow = await AdmitPrepareAndArmAsync(database, host, resource, prepared, finalized,
            expire ? TimeSpan.FromSeconds(6) : TimeSpan.FromMinutes(2));
        Task<JobExternalExecutionResult>? invocation = null;
        try
        {
            invocation = Task.Run(() => flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None));
            await construction.Entered.WaitAsync(TimeSpan.FromSeconds(20));
            construction.ExternalDenial.Should().BeTrue();
            (await provider.CountsAsync(flow.Operation)).Should().Be((0L, 0L, 0L));
            invocation.IsCompleted.Should().BeFalse("the concrete constructor, scope and invocation remain owned");
            if (expire) await WaitForDatabaseLeaseAsync(database, flow.Lease);
            flow.Receipt.Run.IsClosed.Should().BeFalse("no cancellation, native timer or manual gate masks the final method check");
            construction.Release();
            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(20));
            using var assertions = new AssertionScope();
            result.Status.Should().Be(expire ? JobExternalExecutionStatus.EntryRefused : JobExternalExecutionStatus.Reported);
            result.CleanupFailed.Should().BeFalse();
            (await provider.CountsAsync(flow.Operation)).Should().Be(expire ? (0L, 0L, 0L) : (1L, 0L, 1L));
            (result.Observation is not null).Should().Be(!expire);
            flow.Arm.CommitAcknowledged.Should().BeTrue("expiry does not rewrite the physical COMMIT receipt");
            flow.Receipt.Run.TrySealUnusedExternalExecute().Should().Be(expire);
        }
        finally
        {
            construction.Release();
            // A test deadline never substitutes for actual provider/scope cleanup.
            if (invocation is not null) await invocation;
        }
    }
}
