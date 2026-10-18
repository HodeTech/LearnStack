using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobBusinessTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task BackgroundJob_Execution_Restores_Tenant_And_Trace(bool organization, bool cancelWhileHeld)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await BackgroundJobSchemaSeed.SeedTenantsAsync(database.AppConnectionString);
        var org = organization ? SchemaFixture.OrgA1 : (Guid?)null;
        var request = organization ? "OrganizationRoot" : "TenantRoot";
        var incoming = Guid.CreateVersion7();
        var job = await BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            definition: organization ? BackgroundJobRuntimeFixture.OrganizationKey : BackgroundJobRuntimeFixture.TenantKey,
            organization: org, causalActor: BackgroundJobRuntimeFixture.Human, incomingEvent: incoming);
        var prior = new BackgroundJobFixtureContext(SchemaFixture.OrgB1, SchemaFixture.TenantB);
        var probe = new BackgroundJobFixtureProbe();
        await using var host = Fixture.Host(database, probe, prior);
        var accessor = host.Services.GetRequiredService<ITenantContextAccessor>();
        using var caller = new Activity("untrusted-job-caller").SetIdFormat(ActivityIdFormat.W3C).Start();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is "learnstack.jobs" or "learnstack.mediatr",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };
        ActivitySource.AddActivityListener(listener);
        var admission = await AdmitAsync(host.Services, job);
        accessor.Current.Should().BeSameAs(prior);
        Activity.Current.Should().BeSameAs(caller);

        // Admission also reads parameters. Install constructor observations only
        // after its acknowledged owner has positively completed cleanup.
        var observations = new ConcurrentQueue<ConstructionContext>();
        var wrongTrace = new ConcurrentQueue<ConstructionContext>();
        var parameterHook = Fixture.ParameterType.GetField("OnConstruction")!;
        var requestHook = Fixture.RequestType(request).GetField("OnConstruction")!;
        var savedParameterHook = parameterHook.GetValue(null);
        var savedRequestHook = requestHook.GetValue(null);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateContextSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        Task<JobBusinessExecutionResult>? work = null;
        try
        {
            parameterHook.SetValue(null, (Action)(() =>
            {
                Observe("parameter");
                // An independent trace-only oracle control at the genuine
                // constructor boundary, preserving the correct job context.
                if (accessor.Current is JobTenantContext)
                {
                    var saved = Activity.Current;
                    try { Activity.Current = caller; wrongTrace.Enqueue(Capture("trace-control")); }
                    finally { Activity.Current = saved; }
                }
            }));
            requestHook.SetValue(null, (Action)(() => Observe("request")));
            probe.OnConstructed = _ => Observe("handler");
            // The same real parameter constructor, issued before the job owner,
            // sees the caller context. The actual assertion must reject that order.
            _ = Fixture.Parameters();
            observations.TryDequeue(out var wrongOrder).Should().BeTrue();
            Action rejectWrongOrder = () => AssertConstructionContext(wrongOrder!, org, job, incoming);
            rejectWrongOrder.Should().Throw<Xunit.Sdk.XunitException>();
            observations.Should().BeEmpty();

            probe.Body = async (_, _, token) =>
            {
                using var registration = token.Register(() => cancellationSeen.TrySetResult());
                Observe("body");
                entered.TrySetResult();
                if (cancelWhileHeld)
                {
                    await cancellationSeen.Task.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                    Observe("cancelled-body");
                    lateContextSeen.TrySetResult();
                }
                await release.Task.WaitAsync(TimeSpan.FromSeconds(15), CancellationToken.None);
                Observe("late-body");
                return Result.Ok(None.Value);
            };
            var gate = admission.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional);
            work = host.Services.GetRequiredService<JobExecutor>().ExecuteBusinessAsync(admission.Receipt, gate, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            accessor.Current.Should().BeSameAs(prior, "an issued async job does not replace its caller's context");
            Activity.Current.Should().BeSameAs(caller);
            if (cancelWhileHeld)
            {
                await stop.CancelAsync();
                await lateContextSeen.Task.WaitAsync(TimeSpan.FromSeconds(15));
                work.IsCompleted.Should().BeFalse("the actual ignoring-cancellation business task still owns its scope");
                accessor.Current.Should().BeSameAs(prior);
                Activity.Current.Should().BeSameAs(caller);
            }
            release.TrySetResult();
            var result = await work.WaitAsync(TimeSpan.FromSeconds(15));
            result.CleanupFailed.Should().BeFalse();
            result.SourceOwner.CleanupConfirmed.Should().BeTrue();
            result.CommitAcknowledged.Should().Be(!cancelWhileHeld);
            result.KnownRollback.Should().Be(cancelWhileHeld);
            accessor.Current.Should().BeSameAs(prior);
            Activity.Current.Should().BeSameAs(caller);
            var actual = observations.ToArray();
            string[] points = cancelWhileHeld
                ? ["parameter", "request", "handler", "body", "cancelled-body", "late-body"]
                : ["parameter", "request", "handler", "body", "late-body"];
            actual.Select(value => value.Point).Should().Equal(points);
            foreach (var observation in actual) AssertConstructionContext(observation, org, job, incoming);
            wrongTrace.Should().ContainSingle();
            var traceControl = wrongTrace.Single();
            AssertConstructionFacts(traceControl, org, job, incoming);
            Action rejectWrongTrace = () => AssertConstructionTrace(traceControl);
            rejectWrongTrace.Should().Throw<Xunit.Sdk.XunitException>();
            foreach (var observation in actual)
                if (observation.Point is "parameter" or "request")
                {
                    observation.Source.Should().Be("learnstack.jobs");
                    observation.Span.Should().Be(observation.ExecutionSpan);
                }
                else
                {
                    observation.Source.Should().Be("learnstack.mediatr");
                    observation.Span.Should().NotBe(observation.ExecutionSpan, "the sampled pipeline child retains its real parent ancestry");
                }
            actual.Should().OnlyContain(value => value.Trace != caller.TraceId.ToHexString(),
                "stored causal trace, rather than the dispatching caller Activity, is restored before construction");
            (await BackgroundJobRuntimeRows.ReadAsync(database.AppConnectionString, job, org))!.Phase
                .Should().Be(cancelWhileHeld ? "executing" : "completed");
        }
        finally
        {
            release.TrySetResult();
            try
            {
                if (work is not null) await work.WaitAsync(TimeSpan.FromSeconds(20));
            }
            finally
            {
                // A failed bounded join cannot authorize resetting hooks still
                // captured by live work. Completed faulted tasks are safe to detach.
                if (work is null || work.IsCompleted)
                {
                    parameterHook.SetValue(null, savedParameterHook);
                    requestHook.SetValue(null, savedRequestHook);
                    probe.OnConstructed = null;
                }
            }
        }

        void Observe(string point) => observations.Enqueue(Capture(point));

        ConstructionContext Capture(string point)
        {
            var context = accessor.Current;
            var activity = Activity.Current;
            var execution = activity;
            while (execution is not null && execution.OperationName != "job.execution") execution = execution.Parent;
            var jobContext = context as JobTenantContext;
            return new(point, context?.TenantId, context?.OrganizationId,
                context?.UserId, context?.CausalActorUserId, activity?.TraceId.ToHexString(),
                execution?.TraceId.ToHexString(), execution?.ParentSpanId.ToHexString(), context?.GetType(),
                jobContext?.JobId, jobContext?.IncomingEventId, context?.ModuleName, context?.CorrelationId,
                activity?.Source.Name, activity?.SpanId.ToHexString(), execution?.SpanId.ToHexString());
        }
    }

    private sealed record ConstructionContext(string Point, TenantId? Tenant, OrganizationId? Organization,
        UserId? User, UserId? CausalActor, string? Trace, string? ExecutionTrace, string? ExecutionParent, Type? ContextType,
        BackgroundJobId? Job, Guid? Incoming, string? Module, string? Correlation,
        string? Source, string? Span, string? ExecutionSpan);

    private static void AssertConstructionContext(ConstructionContext observation, Guid? organization, BackgroundJobId job, Guid incoming)
    {
        AssertConstructionFacts(observation, organization, job, incoming);
        AssertConstructionTrace(observation);
    }

    private static void AssertConstructionFacts(ConstructionContext observation, Guid? organization, BackgroundJobId job, Guid incoming)
    {
        observation.ContextType.Should().Be<JobTenantContext>();
        observation.Tenant.Should().Be(TenantId.From(SchemaFixture.TenantA));
        observation.Organization.Should().Be(organization is { } org ? OrganizationId.From(org) : null);
        observation.User.Should().Be(UserId.SystemActor);
        observation.CausalActor.Should().Be(UserId.From(BackgroundJobRuntimeFixture.Human));
        observation.Job.Should().Be(job);
        observation.Incoming.Should().Be(incoming);
        observation.Module.Should().Be("jobssubmissionfixture");
        observation.Correlation.Should().Be(BackgroundJobRuntimeFixture.Traceparent);
    }

    private static void AssertConstructionTrace(ConstructionContext observation)
    {
        observation.Trace.Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
        observation.ExecutionTrace.Should().Be(observation.Trace);
        observation.ExecutionParent.Should().Be("00f067aa0ba902b7");
        observation.ExecutionSpan.Should().NotBeNullOrEmpty().And.NotBe("0000000000000000").And.NotBe(observation.ExecutionParent);
    }
}
