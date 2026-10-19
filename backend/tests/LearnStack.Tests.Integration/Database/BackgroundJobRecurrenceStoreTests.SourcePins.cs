using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_recipient_cancellation_or_scope_cleanup_pins_both_sources_after_all_siblings_join(bool scopeCleanup)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using (var app = await PostgresFixture.OpenAsync(database.AppConnectionString))
            await SeedLifecycleTenantAsync(app, SchemaFixture.TenantA, SchemaFixture.OrgA1, "Active", false, true);
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-1), today.AddDays(-1), period: TimeSpan.FromDays(30));
        var observation = new RecipientSourcePinObservation(scopeCleanup);
        var bounds = new BackgroundJobRuntimeOptions { ControlPoll = TimeSpan.FromMilliseconds(50) };
        await using var host = RecipientHost(database, new(), configure: services =>
        {
            services.RemoveAll<BackgroundJobRuntimeOptions>();
            services.AddSingleton(bounds);
            services.AddSingleton<LearnStack.SharedKernel.Audit.IAuditCatalogSource>(new RecurrenceFixtureAuditSource());
            services.AddSingleton(observation);
            services.AddScoped<HeldRecipientScopeDisposal>();
            services.AddScoped<SubmitBackgroundJobRecipientCommandHandler>();
            services.RemoveAll<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>>();
            services.AddScoped<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>, SourcePinRecipientSubmission>();
        });
        await using var source = Source(database);
        using var globals = new RecurrenceGlobalFilters();
        var runtime = RecurrenceRuntime(host.Services, source, Fixture.Recurrences(manifest), bounds);
        var application = host.Services.GetRequiredService<ApplicationDataSourceLifetime>();
        var control = NativeHoldField<BackgroundJobControl>(runtime, "_jobs");
        var recurrence = NativeHoldField<BackgroundJobRecurrenceControl>(control, "_recurrence");
        using var callbackRelease = new ManualResetEventSlim();
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackReturned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<CancellationTokenRegistration>? callbackRegistration = null;
        BackgroundJobRecipientOperation? operation = null;
        try
        {
            await runtime.StartAsync();
            var command = await observation.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            operation = ReadActualRecipient(recurrence, command.Claim.RecipientId.Value);
            var actual = await NativeHoldField<TaskCompletionSource<Task<JobRecipientSubmissionResult>>>(operation, "_published")
                .Task.WaitAsync(TimeSpan.FromSeconds(30));
            if (scopeCleanup)
            {
                await observation.ScopeEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                command.Gate.CommitAcknowledged.Should().BeTrue();
                actual.IsCompleted.Should().BeFalse("the real DI scope has not returned from DisposeAsync");
            }
            else
            {
                // Instrument the actual operation's original CTS. A callback
                // on the handler's linked deadline could make executor cleanup
                // itself wait and hide a missing cancellation-relay join.
                var originalCancellation = NativeHoldField<CancellationTokenSource>(operation, "_cancellation");
                // Registration itself can invoke inline if stop won already.
                // Publish that actual task before waiting, so finally can
                // release and join it even when the finite setup wait expires.
                callbackRegistration = Task.Run(() => originalCancellation.Token.UnsafeRegister(_ =>
                {
                    callbackEntered.TrySetResult();
                    try
                    {
                        if (!callbackRelease.Wait(TimeSpan.FromMinutes(2)))
                            throw new TimeoutException("The recipient cancellation fixture was not released.");
                    }
                    finally { callbackReturned.TrySetResult(); }
                }, null));
                _ = await callbackRegistration.WaitAsync(TimeSpan.FromSeconds(30));
            }
            using var callerWait = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
            var stopping = runtime.StopAsync(callerWait.Token);
            if (!scopeCleanup)
            {
                await callbackEntered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                observation.HandlerRelease.TrySetResult();
                var returned = await actual.WaitAsync(TimeSpan.FromSeconds(30));
                returned.CleanupFailed.Should().BeFalse();
                returned.RetainedCleanup.Should().BeEmpty();
                returned.SourceOwner.CleanupConfirmed.Should().BeTrue();
                command.Gate.CommitAcknowledged.Should().BeFalse();
                NativeHoldField<Task>(operation, "_cancel").IsCompleted.Should().BeFalse();
                callbackReturned.Task.IsCompleted.Should().BeFalse();
            }
            await FluentActions.Awaiting(() => stopping).Should().ThrowAsync<OperationCanceledException>();
            await JoinRecipientSourcePinSiblingsAsync(runtime, control, recurrence);
            await FluentActions.Awaiting(() => operation.Completion.WaitAsync(TimeSpan.FromMilliseconds(250)))
                .Should().ThrowAsync<TimeoutException>();
            control.Completion.IsCompleted.Should().BeFalse();
            runtime.Completion.IsCompleted.Should().BeFalse();
            recurrence.Snapshot.CleanupConfirmed.Should().BeFalse();
            IsActualRecipientRetained(recurrence, operation).Should().BeTrue();
            source.Dispose();
            application.Dispose();
            source.IsDisposalComplete.Should().BeFalse();
            application.IsDisposalComplete.Should().BeFalse();
        }
        finally
        {
            observation.HandlerRelease.TrySetResult();
            observation.ScopeRelease.TrySetResult();
            callbackRelease.Set();
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await runtime.StopAsync(cleanup.Token); await runtime.Completion.WaitAsync(cleanup.Token); }
            finally
            {
                try
                {
                    if (callbackRegistration is not null)
                    {
                        var registration = await callbackRegistration.WaitAsync(TimeSpan.FromSeconds(30));
                        await registration.DisposeAsync();
                    }
                }
                finally
                {
                    if (operation is not null) await operation.Completion.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
        }
        runtime.Snapshot.CleanupConfirmed.Should().BeTrue();
        operation!.Result!.SourceOwner.CleanupConfirmed.Should().BeTrue();
        operation.Result.Command!.Gate.CommitAcknowledged.Should().Be(scopeCleanup);
        if (scopeCleanup) observation.ScopeReturned.Task.IsCompletedSuccessfully.Should().BeTrue();
        else callbackReturned.Task.IsCompletedSuccessfully.Should().BeTrue();
        await RecurrenceEventuallyAsync(() => Task.FromResult(source.IsDisposalComplete && application.IsDisposalComplete));
        source.HasUnconfirmedCleanup.Should().BeFalse();
        application.HasUnconfirmedCleanup.Should().BeFalse();
    }

    private static async Task JoinRecipientSourcePinSiblingsAsync(BackgroundJobRuntime runtime,
        BackgroundJobControl control, BackgroundJobRecurrenceControl recurrence)
    {
        await JoinNativeHoldSiblingsAsync(runtime, control, recurrence);
        BackgroundJobNativeRecurrenceOperation? native;
        lock (NativeHoldField<object>(recurrence, "_sync"))
            native = (BackgroundJobNativeRecurrenceOperation?)typeof(BackgroundJobRecurrenceControl)
                .GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(recurrence);
        if (native is not null) await native.Completion.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class RecipientSourcePinObservation(bool scopeCleanup)
    {
        internal bool ScopeCleanup { get; } = scopeCleanup;
        internal TaskCompletionSource<SubmitBackgroundJobRecipientCommand> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource HandlerRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ScopeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ScopeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ScopeReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class HeldRecipientScopeDisposal(RecipientSourcePinObservation observation) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            observation.ScopeEntered.TrySetResult();
            await observation.ScopeRelease.Task.WaitAsync(TimeSpan.FromMinutes(2));
            observation.ScopeReturned.TrySetResult();
        }
    }

    private sealed class SourcePinRecipientSubmission(SubmitBackgroundJobRecipientCommandHandler actual,
        IServiceProvider services, RecipientSourcePinObservation observation)
        : IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>
    {
        public async Task<Result<BackgroundJobId>> Handle(SubmitBackgroundJobRecipientCommand request, CancellationToken cancellationToken)
        {
            if (observation.ScopeCleanup) _ = services.GetRequiredService<HeldRecipientScopeDisposal>();
            observation.Entered.TrySetResult(request);
            if (!observation.ScopeCleanup)
                await observation.HandlerRelease.Task.WaitAsync(TimeSpan.FromMinutes(2), CancellationToken.None);
            return await actual.Handle(request, cancellationToken);
        }
    }
}
