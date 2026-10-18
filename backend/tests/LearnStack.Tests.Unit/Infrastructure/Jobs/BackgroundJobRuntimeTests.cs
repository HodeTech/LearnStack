using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Reflection;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobRuntimeTests : IDisposable
{
    private readonly List<BackgroundJobObservation> _observations = [];
    public void Dispose() { foreach (var observation in _observations) observation.Dispose(); }

    [Fact]
    public async Task Stop_before_start_joins_positive_unissued_ownership_without_resolving_the_source()
    {
        using var source = Source();
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var runtime = Runtime(source, definitions, gate, new(false, 1));
        runtime.Snapshot.State.Should().Be(BackgroundJobRuntimeState.NotStarted);
        await runtime.StopAsync();
        runtime.Completion.IsCompletedSuccessfully.Should().BeTrue();
        runtime.Snapshot.State.Should().Be(BackgroundJobRuntimeState.Stopped);
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        runtime.Snapshot.BootstrapJoined.Should().BeTrue("no bootstrap invocation was ever issued");
        source.IsValueCreated.Should().BeFalse();
        gate.IsOpen.Should().BeFalse();
        await FluentActions.Awaiting(() => runtime.StartAsync()).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task An_already_canceled_start_closes_the_gate_before_any_bootstrap_acquisition()
    {
        using var source = Source();
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var runtime = Runtime(source, definitions, gate, new(false, 1));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await FluentActions.Awaiting(() => runtime.StartAsync(canceled.Token)).Should().ThrowAsync<OperationCanceledException>();
        await runtime.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        source.IsValueCreated.Should().BeFalse();
        gate.IsOpen.Should().BeFalse();
    }

    [Fact]
    public void Capacity_refusal_precedes_source_resolution_or_native_owner_startup()
    {
        using var source = Source();
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var construct = () => Runtime(source, definitions, gate, new(true, 49));
        construct.Should().Throw<ArgumentException>();
        source.IsValueCreated.Should().BeFalse();
        gate.IsOpen.Should().BeTrue();
    }

    [Fact]
    public async Task Missing_messaging_binding_refuses_start_and_does_not_release_the_shared_source()
    {
        await using var source = Source();
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var runtime = Runtime(source, definitions, gate, new(false, 1), bindMessaging: false);
        await FluentActions.Awaiting(() => runtime.StartAsync()).Should().ThrowAsync<InvalidOperationException>();
        source.IsValueCreated.Should().BeFalse();
        await runtime.StopAsync();
        runtime.Completion.IsCompletedSuccessfully.Should().BeTrue();
        runtime.Completion.AsyncState.Should().BeSameAs(runtime);
        await source.DisposeAsync();
        source.IsDisposalComplete.Should().BeFalse("an absent binding is not proof that messaging source users joined");
        // This direct fixture has issued neither messaging owner.
        runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        await SourceObserver(source).WaitAsync(TimeSpan.FromSeconds(5));
        source.IsDisposalComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Source_disposal_waits_for_both_positive_messaging_witnesses_after_runtime_completion()
    {
        await using var source = Source();
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var gate = new BackgroundJobRuntimeGate(definitions.Infrastructure);
        var runtime = Runtime(source, definitions, gate, new(false, 1), bindMessaging: false);
        var outbox = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delivery = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.BindMessagingLifetime(outbox.Task, delivery.Task);
        await runtime.StopAsync();
        await source.DisposeAsync();
        source.IsDisposalComplete.Should().BeFalse();
        outbox.SetResult();
        source.IsDisposalComplete.Should().BeFalse("the independently held delivery owner still needs this source");
        delivery.SetResult();
        await SourceObserver(source).WaitAsync(TimeSpan.FromSeconds(5));
        source.IsDisposalComplete.Should().BeTrue();
        source.IsValueCreated.Should().BeFalse();
    }

    private static Task SourceObserver(PrivilegedDataSource source) =>
        (Task)typeof(PrivilegedDataSource).GetField("_retentionObserver", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(source)!;

    private static PrivilegedDataSource Source() => new(() =>
        throw new InvalidOperationException("The unissued runtime must not resolve its source."));

    private BackgroundJobRuntime Runtime(PrivilegedDataSource source,
        BackgroundJobDefinitionRegistry definitions, BackgroundJobRuntimeGate gate, BackgroundJobPhysicalPoolSettings settings,
        bool bindMessaging = true)
    {
        var bounds = new BackgroundJobRuntimeOptions();
        var observation = new BackgroundJobObservation(definitions, TimeProvider.System);
        _observations.Add(observation);
        var executor = new JobExecutor(Substitute.For<IServiceScopeFactory>(), Substitute.For<ITenantContextAccessor>(),
            new BackgroundJobExecutionAdmission(definitions, definitions, Substitute.For<IAuditCatalog>()), bounds, observation);
        var routing = new PostgresBackgroundJobRoutingStore(source, definitions, bounds, new SystemGuidFactory(), gate);
        var runtime = new BackgroundJobRuntime(source, definitions, BackgroundJobRecurrenceRegistry.CreateEmpty(), executor, routing, gate,
            bounds, new SystemGuidFactory(), TimeProvider.System, settings, new HangfireBoundedLogProvider());
        // This direct fixture creates neither API messaging processor.
        if (bindMessaging) runtime.BindMessagingLifetime(Task.CompletedTask, Task.CompletedTask);
        return runtime;
    }
}
