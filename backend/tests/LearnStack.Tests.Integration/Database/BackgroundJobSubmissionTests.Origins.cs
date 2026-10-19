using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobSubmissionTests
{
    [Fact]
    public async Task A_child_submission_preserves_causal_human_and_incoming_event_while_keeping_parent_and_job_id_distinct()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var incoming = Guid.CreateVersion7();
        var parent = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            causalActor: BackgroundJobRuntimeFixture.Human, incomingEvent: incoming);
        var context = new BackgroundJobFixtureContext
        {
            UserId = UserId.SystemActor,
            CausalActorUserId = UserId.From(BackgroundJobRuntimeFixture.Human),
        };
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe, context);
        var claim = (await host.Services.GetRequiredService<PostgresBackgroundJobRoutingStore>().TryClaimAsync(parent, 1))!;
        var start = await host.Services.GetRequiredService<JobExecutor>().ExecuteAsync(claim, BackgroundJobTestRun.Create());
        start.Receipt.Should().NotBeNull();
        BackgroundJobId? child = null;
        string? producerTrace = null;
        probe.Body = async (request, services, _) =>
        {
            request.Should().Be("TenantRoot");
            services.GetRequiredService<LearnStack.SharedKernel.Tenancy.ITenantContext>().UserId.Should().Be(UserId.SystemActor);
            producerTrace = Activity.Current!.Id;
            child = await Fixture.EnqueueAsync(services);
            var pending = (await BackgroundJobRuntimeRows.ReadInUnitAsync(services.GetRequiredService<IUnitOfWork>(), child.Value))!;
            pending.ParentJob.Should().Be(parent.Value);
            pending.IncomingEvent.Should().Be(incoming);
            pending.CausalActor.Should().Be(BackgroundJobRuntimeFixture.Human);
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, child.Value)).Should().BeNull(
                "the child stays inside the parent business transaction until its completion is acknowledged");
            return Result.Ok(None.Value);
        };
        var result = await host.Services.GetRequiredService<JobExecutor>()
            .ExecuteBusinessAsync(start.Receipt!, start.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional));
        result.CommitAcknowledged.Should().BeTrue();
        result.CleanupFailed.Should().BeFalse();
        result.SourceOwner.Should().BeSameAs(start.SourceOwner);
        result.SourceOwner.CleanupConfirmed.Should().BeTrue();
        (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, parent))!.Phase.Should().Be("completed");
        var row = (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, child!.Value))!;
        row.Id.Should().NotBe(parent.Value).And.NotBe(incoming);
        row.ParentJob.Should().Be(parent.Value);
        row.IncomingEvent.Should().Be(incoming);
        row.CausalActor.Should().Be(BackgroundJobRuntimeFixture.Human);
        row.Traceparent.Should().Be(producerTrace);
        context.UserId.Should().Be(UserId.SystemActor, "causal identity never lends human authority");
    }

    [Theory]
    [InlineData("constructor")]
    [InlineData("clock")]
    [InlineData("identifier")]
    public async Task Submission_rechecks_the_real_physical_generation_after_each_arbitrary_callback(string seam)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var probe = new BackgroundJobFixtureProbe();
        var clock = new CallbackClock();
        var ids = new CallbackIds();
        var constructor = Fixture.ParameterType.GetField("OnConstruction")!;
        var stop = new InvalidOperationException("End real stale-generation proof");
        var callbacks = 0;
        var observed = false;
        await using var host = Fixture.Host(database, probe, configure: services =>
        {
            services.AddSingleton<IClock>(clock);
            services.AddSingleton<IGuidFactory>(ids);
        });
        await using var scope = host.Services.CreateAsyncScope();
        probe.Body = async (_, services, _) =>
        {
            var unit = services.GetRequiredService<IUnitOfWork>();
            var original = services.GetRequiredService<ProducerExecution>().RequireAnnouncement();
            var parameters = Fixture.Parameters();
            IUnitOfWorkScope? replacement = null;
            void ReplaceTransaction()
            {
                constructor.SetValue(null, null);
                clock.Callback = null;
                ids.Callback = null;
                callbacks++;
                unit.CommitAsync(CancellationToken.None).GetAwaiter().GetResult();
                replacement = unit.BeginTransactionAsync(CancellationToken.None).GetAwaiter().GetResult();
                unit.SetTenantContextAsync(new BackgroundJobFixtureContext(), CancellationToken.None).GetAwaiter().GetResult();
                unit.GetTenantContextAnnouncement(unit.Transaction)!.Generation.Should().BeGreaterThan(original.Generation);
            }
            try
            {
                if (seam == "constructor") constructor.SetValue(null, (Action)ReplaceTransaction);
                if (seam == "clock") clock.Callback = ReplaceTransaction;
                if (seam == "identifier") ids.Callback = ReplaceTransaction;
                await FluentActions.Awaiting(() => Fixture.EnqueueAsync(services, parameters: parameters)).Should()
                    .ThrowAsync<InvalidOperationException>().WithMessage("*enrolled writable frame*");
                callbacks.Should().Be(1);
                CurrentInvocation(services).HasProducedWork.Should().BeFalse();
                (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT count(*) FROM background_jobs")).Should().Be(0L,
                    "the later announced physical transaction must receive no job INSERT from the retained producer");
                observed = true;
                throw stop;
            }
            finally
            {
                constructor.SetValue(null, null);
                clock.Callback = null;
                ids.Callback = null;
                if (replacement is not null) await replacement.DisposeAsync();
            }
        };
        (await FluentActions.Awaiting(() => Fixture.SendAsync(scope.ServiceProvider)).Should()
            .ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(stop);
        observed.Should().BeTrue();
        AssertClean(scope.ServiceProvider);
    }

    private sealed class CallbackClock : IClock
    {
        internal Action? Callback { get; set; }
        public DateTimeOffset UtcNow { get { Callback?.Invoke(); return DateTimeOffset.UtcNow; } }
    }

    private sealed class CallbackIds : IGuidFactory
    {
        internal Action? Callback { get; set; }
        public Guid NewUuidV7() { Callback?.Invoke(); return Guid.CreateVersion7(); }
        public Guid NewUuidV4() => Guid.NewGuid();
    }
}
