using System.Diagnostics;
using LearnStack.Application.Jobs;
using LearnStack.Modules.Tenancy.Infrastructure.Jobs;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Fact]
    public async Task Recipient_Start_observer_fault_disposes_the_exact_owned_span_without_creating_a_child()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await using var host = RecipientHost(database, new());
        var observation = host.Services.GetRequiredService<BackgroundJobObservation>();
        using var metrics = new BackgroundJobStageProbe(observation);
        using var traces = new BackgroundJobTraceProbe(observation, _ => throw new InvalidOperationException("Deliberate recipient Start fault."));
        var previous = Activity.Current;
        var accessor = host.Services.GetRequiredService<ITenantContextAccessor>();
        var context = accessor.Current;
        var gate = BackgroundJobTestRun.Create().OpenBoundary(BackgroundJobCheckpointKind.RecipientSubmission);

        var result = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), gate, CancellationToken.None);

        result.Status.Should().Be(JobRecipientSubmissionStatus.Failed);
        result.Failure.Should().BeOfType<InvalidOperationException>();
        result.CleanupFailed.Should().BeFalse();
        result.RetainedCleanup.Should().BeEmpty();
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        gate.CommitStarted.Should().BeFalse();
        var span = traces.Started.Should().ContainSingle().Which;
        traces.Stopped.Should().ContainSingle().Which.Should().BeSameAs(span,
            "the concrete Activity must be retained before Start can invoke arbitrary observers");
        Activity.Current.Should().BeSameAs(previous);
        accessor.Current.Should().BeSameAs(context);
        metrics.Duration("recipient_submission").Tags["outcome"].Should().Be("Failed");
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0);
    }
}
