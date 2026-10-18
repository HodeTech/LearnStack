using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed partial class BackgroundJobObservationTests
{
    [Theory]
    [InlineData("occurrence", "ack", false)]
    [InlineData("occurrence", "ack", true)]
    [InlineData("occurrence", "uncertain", false)]
    [InlineData("occurrence", "rollback", false)]
    [InlineData("page", "ack", false)]
    [InlineData("page", "ack", true)]
    [InlineData("page", "uncertain", true)]
    [InlineData("page", "rollback", false)]
    [InlineData("empty_completed_page", "ack", false)]
    [InlineData("null", "ack", false)]
    [InlineData("registration", "ack", false)]
    public async Task Actual_recurrence_owner_accounts_ACK_and_typed_progress_before_resource_cleanup_qualification(
        string kind, string commit, bool failCleanup)
    {
        await using var fixture = new Fixture();
        var definitions = BackgroundJobDefinitionRegistry.CreateEmpty();
        var bounds = new BackgroundJobRuntimeOptions();
        var executor = new JobExecutor(Substitute.For<IServiceScopeFactory>(), Substitute.For<ITenantContextAccessor>(),
            new(definitions, definitions, Substitute.For<IAuditCatalog>()), bounds, fixture.Observer);
        var owner = new BackgroundJobRecurrenceControl(fixture.Source, definitions, BackgroundJobRecurrenceRegistry.CreateEmpty(),
            null, executor, fixture.Gate, bounds, new SystemGuidFactory(), fixture.Time);
        object? value = kind switch
        {
            "occurrence" => new BackgroundJobMaterializedOccurrence(
                BackgroundJobOccurrenceId.From(Guid.Parse("019712ac-1234-7000-8000-0000000000ab")),
                "jobsfixture.scan", 1, DateTimeOffset.UnixEpoch, 7),
            "page" => new BackgroundJobOccurrencePage(Guid.NewGuid(), 9, 4, false),
            "empty_completed_page" => new BackgroundJobOccurrencePage(Guid.NewGuid(), 0, 0, true),
            "registration" => BackgroundJobRecurrenceRegistrationResult.Registered,
            _ => null,
        };
        var resources = new BackgroundJobControlResources
        {
            CommitAttempted = commit != "rollback",
            CommitAcknowledged = commit == "ack",
            AuditScope = failCleanup ? new AsyncServiceScope(new RefusedRecurrenceCleanup()) : null,
        };
        // Synthetic ACK facts exercise the actual resource model and owner;
        // they do not simulate a PostgreSQL transaction/connection dispose fault.
        await resources.CleanupAsync();
        resources.CleanupConfirmed.Should().Be(!failCleanup);
        if (failCleanup) resources.Failure.Should().NotBeNull();
        else resources.Failure.Should().BeNull();
        var operation = new OwnedBackgroundJobControl<object>(value, resources, []);
        var flight = Task.FromResult(operation);
        var observe = typeof(BackgroundJobRecurrenceControl).GetMethod("ObserveSqlAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(typeof(object));
        try
        {
            var result = await ((Task<OwnedBackgroundJobControl<object>?>)observe.Invoke(owner, [flight, 1])!);
            result.Should().Be(failCleanup || commit == "uncertain" ? null : operation);
            var snapshot = owner.Snapshot;
            var ack = commit == "ack";
            snapshot.OccurrencesAcknowledged.Should().Be(ack && kind == "occurrence" ? 1 : 0);
            snapshot.SkippedSlotsAcknowledged.Should().Be(ack && kind == "occurrence" ? 7 : 0);
            snapshot.PagesAcknowledged.Should().Be(ack && kind is "page" or "empty_completed_page" ? 1 : 0);
            snapshot.RoutesExaminedAcknowledged.Should().Be(ack && kind == "page" ? 9 : 0);
            snapshot.RecipientsInsertedAcknowledged.Should().Be(ack && kind == "page" ? 4 : 0);
            snapshot.FanoutsCompletedAcknowledged.Should().Be(ack && kind == "empty_completed_page" ? 1 : 0);
            if (failCleanup)
            {
                snapshot.SqlHealthy.Should().BeFalse();
                typeof(BackgroundJobRecurrenceControl).GetField("_sqlOperation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(owner).Should().BeSameAs(operation);
                typeof(BackgroundJobRecurrenceControl).GetField("_sqlFlight", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(owner).Should().BeSameAs(flight);
            }
            fixture.Source.IsValueCreated.Should().BeFalse();
        }
        finally
        {
            owner.RequestStop();
            await ((Task)typeof(BackgroundJobRecurrenceControl)
                .GetField("_completionObservation", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!)
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        owner.Completion.IsCompletedSuccessfully.Should().Be(!failCleanup);
    }

    private sealed class RefusedRecurrenceCleanup : IServiceScope, IAsyncDisposable
    {
        public IServiceProvider ServiceProvider => throw new InvalidOperationException("This cleanup control resolves no service.");
        public void Dispose() => throw new InvalidOperationException("Deliberate recurrence resource cleanup refusal.");
        public ValueTask DisposeAsync() => ValueTask.FromException(new InvalidOperationException("Deliberate recurrence resource cleanup refusal."));
    }
}
