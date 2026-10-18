using System.Reflection;
using FluentAssertions;
using Hangfire;
using Hangfire.Logging;
using Hangfire.PostgreSql;
using Hangfire.Server;
using Hangfire.Storage;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class PoolHangfireStorageTests
{
    private static readonly string[] StorageMemberNames =
    [
        "GetComponents", "GetConnection", "GetMonitoringApi", "GetReadOnlyConnection", "GetServerRequiredProcesses",
        "GetStateHandlers", "GetStorageWideProcesses", "HasFeature", "WriteOptionsToLog", "get_LinearizableReads",
    ];

    private static readonly string[] ConnectionMemberNames =
    [
        "AcquireDistributedLock/2", "AnnounceServer/2", "CreateExpiredJob/4", "CreateWriteTransaction/0", "Dispose/0",
        "FetchNextJob/2", "GetAllEntriesFromHash/1", "GetAllItemsFromList/1", "GetAllItemsFromSet/1", "GetCounter/1",
        "GetFirstByLowestScoreFromSet/3", "GetFirstByLowestScoreFromSet/4", "GetHashCount/1", "GetHashTtl/1",
        "GetJobData/1", "GetJobParameter/2", "GetListCount/1", "GetListTtl/1", "GetRangeFromList/3", "GetRangeFromSet/3",
        "GetSetContains/2", "GetSetCount/1", "GetSetCount/2", "GetSetTtl/1", "GetStateData/1", "GetUtcDateTime/0",
        "GetValueFromHash/2", "Heartbeat/1", "RemoveServer/1", "RemoveTimedOutServers/1", "SetJobParameter/3", "SetRangeInHash/2",
    ];

    [Fact]
    public void Every_pinned_public_connection_member_has_an_explicit_delegating_override()
    {
        var methods = typeof(JobStorageConnection).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
        methods.Select(method => $"{method.Name}/{method.GetParameters().Length}").Order(StringComparer.Ordinal).Should().Equal(
            ConnectionMemberNames.Order(StringComparer.Ordinal));
        foreach (var method in methods)
            typeof(PoolHangfireConnection).GetMethod(method.Name, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray())!
                .DeclaringType.Should().Be<PoolHangfireConnection>($"{method.Name} must not fall back to a base implementation");
    }

    [Fact]
    public void Every_pinned_virtual_storage_member_is_forwarded_and_nonvirtual_expiration_is_guarded()
    {
        var methods = typeof(JobStorage).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(method => method.IsVirtual).ToArray();
        methods.Select(method => method.Name).Order(StringComparer.Ordinal).Should().Equal(StorageMemberNames.Order(StringComparer.Ordinal));
        foreach (var method in methods)
            typeof(PoolHangfireStorage).GetMethod(method.Name, method.GetParameters().Select(parameter => parameter.ParameterType).ToArray())!
                .DeclaringType.Should().Be<PoolHangfireStorage>();

        var provider = Provider();
        provider.JobExpirationTimeout = TimeSpan.FromDays(2);
        var pool = new PoolHangfireStorage(provider, new RecordingLifecycle(), []);
        pool.JobExpirationTimeout.Should().Be(provider.JobExpirationTimeout);
        pool.JobExpirationTimeout = TimeSpan.FromDays(3);
        using var connection = (JobStorageConnection)pool.GetConnection();
        var divergent = () => connection.GetUtcDateTime();
        divergent.Should().Throw<InvalidOperationException>();
        pool.JobExpirationTimeout = TimeSpan.FromDays(2);
        provider.JobExpirationTimeout = TimeSpan.FromDays(4);
        divergent.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("announce", false)]
    [InlineData("announce", true)]
    [InlineData("heartbeat", false)]
    [InlineData("heartbeat", true)]
    [InlineData("remove", false)]
    [InlineData("remove", true)]
    public void Expiration_drift_is_contained_inside_the_lifecycle_before_provider_acquisition(string method, bool providerDrift)
    {
        var provider = Provider();
        var lifecycle = new RecordingLifecycle();
        var pool = new PoolHangfireStorage(provider, lifecycle, []);
        if (providerDrift) provider.JobExpirationTimeout += TimeSpan.FromDays(1);
        else pool.JobExpirationTimeout += TimeSpan.FromDays(1);
        using var connection = (JobStorageConnection)pool.GetConnection();

        var invoke = () => Invoke(connection, method);
        invoke.Should().NotThrow();
        lifecycle.Result!.Succeeded.Should().BeFalse();
        lifecycle.Result.Failure.Should().BeOfType<InvalidOperationException>();
        lifecycle.Result.DisposalFailure.Should().BeNull();
        provider.DidNotReceive().GetConnection();
    }

    [Fact]
    public void Selected_component_instances_are_frozen_without_reexecuting_the_provider_factory()
    {
        var provider = Provider();
#pragma warning disable CS0618 // Exercise the pinned component contract explicitly.
        var first = Substitute.For<IServerComponent>();
        var second = Substitute.For<IServerComponent>();
        var selected = new List<IServerComponent> { first, second };
        var pool = new PoolHangfireStorage(provider, new RecordingLifecycle(), selected);
        selected.Clear();
        pool.GetComponents().Should().Equal(first, second);
        pool.GetComponents().First().Should().BeSameAs(first);
        provider.DidNotReceive().GetComponents();
#pragma warning restore CS0618
    }

    [Fact]
    public void Non_lifecycle_calls_preserve_original_transaction_fetched_job_lock_and_extended_result_objects()
    {
        var provider = Substitute.For<JobStorage>();
        var inner = Substitute.For<JobStorageConnection>();
        var transaction = Substitute.For<IWriteOnlyTransaction>();
        var fetched = Substitute.For<IFetchedJob>();
        var heldLock = Substitute.For<IDisposable>();
        var batch = new List<string> { "17", "23" };
        var queues = new[] { "transactional" };
        provider.GetConnection().Returns(inner);
        inner.CreateWriteTransaction().Returns(transaction);
        inner.FetchNextJob(queues, CancellationToken.None).Returns(fetched);
        inner.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5)).Returns(heldLock);
        inner.GetFirstByLowestScoreFromSet("schedule", 0, 10, 2).Returns(batch);
        var wrapper = new PoolHangfireConnection(provider, new RecordingLifecycle(), readOnly: false);
        provider.DidNotReceive().GetConnection();
        wrapper.CreateWriteTransaction().Should().BeSameAs(transaction);
        wrapper.FetchNextJob(queues, CancellationToken.None).Should().BeSameAs(fetched);
        wrapper.AcquireDistributedLock("resource", TimeSpan.FromSeconds(5)).Should().BeSameAs(heldLock);
        wrapper.GetFirstByLowestScoreFromSet("schedule", 0, 10, 2).Should().BeSameAs(batch);
        wrapper.Dispose();
        wrapper.Dispose();
        provider.Received(1).GetConnection();
        inner.Received(1).Dispose();
        transaction.DidNotReceive().Dispose();
        fetched.DidNotReceive().Dispose();
        heldLock.DidNotReceive().Dispose();
    }

    [Fact]
    public void Read_only_connections_and_feature_diagnostics_use_the_original_provider_seams()
    {
        var provider = Provider();
        var readOnly = Substitute.For<JobStorageConnection>();
        var logger = Substitute.For<ILog>();
        provider.GetReadOnlyConnection().Returns(readOnly);
        provider.HasFeature("fixture.feature").Returns(true);
        provider.LinearizableReads.Returns(true);
        var pool = new PoolHangfireStorage(provider, new RecordingLifecycle(), []);
        pool.HasFeature("fixture.feature").Should().BeTrue();
        pool.LinearizableReads.Should().BeTrue();
        pool.WriteOptionsToLog(logger);
        using var wrapper = pool.GetReadOnlyConnection();
        wrapper.GetJobParameter("17", "RetryCount");
        provider.Received(1).GetReadOnlyConnection();
        provider.DidNotReceive().GetConnection();
        provider.Received(1).WriteOptionsToLog(logger);
    }

    [Fact]
    public void Ordinary_disposal_failure_is_not_repeated_and_the_wrapper_remains_closed()
    {
        var provider = Substitute.For<JobStorage>();
        var inner = Substitute.For<JobStorageConnection>();
        provider.GetConnection().Returns(inner);
        inner.When(connection => connection.Dispose()).Do(_ => throw new InvalidOperationException("fixture disposal"));
        var wrapper = new PoolHangfireConnection(provider, new RecordingLifecycle(), readOnly: false);
        wrapper.GetCounter("stats:succeeded");
        var dispose = () => wrapper.Dispose();
        dispose.Should().Throw<InvalidOperationException>();
        wrapper.Dispose();
        inner.Received(1).Dispose();
        var useAfterDisposal = () => wrapper.GetCounter("stats:succeeded");
        useAfterDisposal.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public void Read_only_lifecycle_acquisition_begins_only_when_the_owner_executes_its_operation()
    {
        var provider = Substitute.For<JobStorage>();
        var inner = Substitute.For<JobStorageConnection>();
        provider.GetReadOnlyConnection().Returns(inner);
        var lifecycle = new RecordingLifecycle(execute: false);
        var wrapper = new PoolHangfireConnection(provider, lifecycle, readOnly: true);
        wrapper.Heartbeat("server");
        provider.DidNotReceive().GetReadOnlyConnection();
        lifecycle.Operation!.Phase.Should().Be(PoolHangfireLifecyclePhase.Created);
        lifecycle.Operation.Attempts.Should().Be(0);
        lifecycle.Operation!.ExecuteAttempt().Succeeded.Should().BeTrue();
        lifecycle.Operation.Phase.Should().Be(PoolHangfireLifecyclePhase.Returned);
        lifecycle.Operation.Attempts.Should().Be(1);
        provider.Received(1).GetReadOnlyConnection();
        provider.DidNotReceive().GetConnection();
        inner.Received(1).Heartbeat("server");
        wrapper.Dispose();
        wrapper.Dispose();
        inner.Received(1).Dispose();
    }

    [Theory]
    [InlineData("announce", "acquire")]
    [InlineData("announce", "invoke")]
    [InlineData("announce", "dispose")]
    [InlineData("heartbeat", "acquire")]
    [InlineData("heartbeat", "invoke")]
    [InlineData("heartbeat", "dispose")]
    [InlineData("remove", "acquire")]
    [InlineData("remove", "invoke")]
    [InlineData("remove", "dispose")]
    public void Lifecycle_acquisition_invocation_and_disposal_faults_all_remain_inside_the_owner_boundary(string method, string fault)
    {
        var provider = Substitute.For<JobStorage>();
        var inner = Substitute.For<JobStorageConnection>();
        var failure = new InvalidOperationException("fixture fault");
        var lifecycle = new RecordingLifecycle();
        using var source = new PrivilegedDataSource(() =>
        {
            lifecycle.Operation!.Phase.Should().Be(PoolHangfireLifecyclePhase.PhysicalOpening);
            throw failure;
        });
        var factory = new HangfireConnectionFactory(source);
        provider.GetConnection().Returns(_ =>
        {
            lifecycle.Operation!.Phase.Should().Be(PoolHangfireLifecyclePhase.Acquiring);
            return fault == "acquire" ? throw failure : inner;
        });
        void InvokeProvider()
        {
            lifecycle.Operation!.Phase.Should().Be(PoolHangfireLifecyclePhase.Invoking);
            if (fault == "invoke") factory.GetOrCreateConnection();
        }
        inner.When(connection => connection.AnnounceServer(Arg.Any<string>(), Arg.Any<ServerContext>())).Do(_ => InvokeProvider());
        inner.When(connection => connection.Heartbeat(Arg.Any<string>())).Do(_ => InvokeProvider());
        inner.When(connection => connection.RemoveServer(Arg.Any<string>())).Do(_ => InvokeProvider());
        inner.When(connection => connection.Dispose()).Do(_ =>
        {
            lifecycle.Operation!.Phase.Should().Be(PoolHangfireLifecyclePhase.Disposing);
            if (fault == "dispose") throw failure;
        });
        var wrapper = new PoolHangfireConnection(provider, lifecycle, readOnly: false);
        provider.DidNotReceive().GetConnection();
        Invoke(wrapper, method);
        lifecycle.Result.Should().NotBeNull();
        lifecycle.Result!.Succeeded.Should().BeFalse();
        (fault == "dispose" ? lifecycle.Result.DisposalFailure : lifecycle.Result.Failure).Should().BeSameAs(failure);
        lifecycle.Operation!.RetainsConnection.Should().Be(fault == "dispose");
        lifecycle.Operation.Phase.Should().Be(PoolHangfireLifecyclePhase.Returned);
        lifecycle.Operation.Attempts.Should().Be(1);
        if (fault == "invoke")
        {
            // A cached source-factory refusal outside the completed lifecycle
            // must not mutate its diagnostic through a leaked thread slot.
            var outside = () => factory.GetOrCreateConnection();
            outside.Should().Throw<InvalidOperationException>().Which.Should().BeSameAs(failure);
            lifecycle.Operation.Phase.Should().Be(PoolHangfireLifecyclePhase.Returned);
        }
        wrapper.Dispose();
        wrapper.Dispose();
        inner.Received(fault == "acquire" ? 0 : 1).Dispose();
        if (method != "heartbeat" || fault == "dispose")
        {
            var repeat = () => lifecycle.Operation!.ExecuteAttempt();
            repeat.Should().Throw<InvalidOperationException>();
            provider.Received(1).GetConnection();
        }
    }

    [Fact]
    public void Heartbeat_retries_acquire_fresh_connections_only_after_completed_disposal()
    {
        var provider = Substitute.For<JobStorage>();
        var first = Substitute.For<JobStorageConnection>();
        var second = Substitute.For<JobStorageConnection>();
        provider.GetConnection().Returns(first, second);
        first.When(connection => connection.Heartbeat("server")).Do(_ => throw new TimeoutException());
        var lifecycle = new RecordingLifecycle();
        using var wrapper = new PoolHangfireConnection(provider, lifecycle, readOnly: false);
        wrapper.Heartbeat("server");
        first.Received(1).Dispose();
        lifecycle.Operation!.Attempts.Should().Be(1);
        lifecycle.Operation.Phase.Should().Be(PoolHangfireLifecyclePhase.Returned);
        lifecycle.Operation!.ExecuteAttempt().Succeeded.Should().BeTrue();
        lifecycle.Operation.Attempts.Should().Be(2);
        lifecycle.Operation.Phase.Should().Be(PoolHangfireLifecyclePhase.Returned);
        second.Received(1).Heartbeat("server");
        second.Received(1).Dispose();
        var repeat = () => lifecycle.Operation!.ExecuteAttempt();
        repeat.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_failed_disposal_preserves_both_faults_and_refuses_another_acquisition()
    {
        var provider = Substitute.For<JobStorage>();
        var inner = Substitute.For<JobStorageConnection>();
        var operationFailure = new BackgroundServerGoneException();
        var disposalFailure = new InvalidOperationException("fixture disposal");
        provider.GetConnection().Returns(inner);
        inner.When(connection => connection.Heartbeat("server")).Do(_ => throw operationFailure);
        inner.When(connection => connection.Dispose()).Do(_ => throw disposalFailure);
        var lifecycle = new RecordingLifecycle();
        using var wrapper = new PoolHangfireConnection(provider, lifecycle, readOnly: false);
        wrapper.Heartbeat("server");
        lifecycle.Result!.Failure.Should().BeSameAs(operationFailure);
        lifecycle.Result.DisposalFailure.Should().BeSameAs(disposalFailure);
        var repeat = () => lifecycle.Operation!.ExecuteAttempt();
        repeat.Should().Throw<InvalidOperationException>();
        provider.Received(1).GetConnection();
        inner.Received(1).Dispose();
    }

    private static PostgreSqlStorage Provider() => Substitute.For<PostgreSqlStorage>(
        Substitute.For<IConnectionFactory>(), HangfireStorageBootstrap.Options());

    private static void Invoke(JobStorageConnection connection, string method)
    {
        switch (method)
        {
            case "announce": connection.AnnounceServer("server", new ServerContext { WorkerCount = 1, Queues = ["transactional"] }); break;
            case "heartbeat": connection.Heartbeat("server"); break;
            case "remove": connection.RemoveServer("server"); break;
            default: throw new ArgumentOutOfRangeException(nameof(method));
        }
    }

    private sealed class RecordingLifecycle(bool execute = true) : PoolHangfireLifecycle
    {
        internal PoolHangfireLifecycleOperation? Operation { get; private set; }
        internal PoolHangfireLifecycleResult? Result { get; private set; }
        internal override void AnnounceServer(PoolHangfireLifecycleOperation operation) => Execute(operation);
        internal override void Heartbeat(PoolHangfireLifecycleOperation operation) => Execute(operation);
        internal override void RemoveServer(PoolHangfireLifecycleOperation operation) => Execute(operation);
        private void Execute(PoolHangfireLifecycleOperation operation)
        {
            Operation = operation;
            if (execute) Result = operation.ExecuteAttempt();
        }
    }
}
