using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Persistence;

public sealed class PrivilegedDataSourceRetentionTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly TimeSpan TestBudget = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Pending_disposal_keeps_lazy_source_available_until_positive_completion(bool asyncDisposal)
    {
        await using var test = new TestSource();
        var positive = Signal();
        test.Source.RetainBackgroundJobsUntil(positive.Task);
        var observer = Observer(test.Source);

        await RequestDisposalAsync(test.Source, asyncDisposal);
        test.Source.IsValueCreated.Should().BeFalse();
        test.Source.IsDisposalComplete.Should().BeFalse();
        observer.IsCompleted.Should().BeFalse();
        test.DisposalCalls.Should().Be(0);

        await AssertSourceStillAvailableAsync(test.Source);
        test.Source.IsValueCreated.Should().BeTrue();
        test.DisposalCalls.Should().Be(0);
        positive.SetResult();
        await observer.WaitAsync(TestBudget);

        test.Source.IsDisposalComplete.Should().BeTrue();
        test.Source.HasUnconfirmedCleanup.Should().BeFalse();
        test.DisposalCalls.Should().Be(1);
        var open = async () => await test.Source.OpenConnectionAsync(new CancellationToken(canceled: true));
        await open.Should().ThrowAsync<ObjectDisposedException>();
        test.Source.Dispose();
        await test.Source.DisposeAsync();
        Observer(test.Source).Should().BeSameAs(observer);
        test.DisposalCalls.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precompleted_no_issued_work_witness_still_requires_a_disposal_request(bool warm)
    {
        await using var test = new TestSource();
        test.Source.RetainBackgroundJobsUntil(Task.CompletedTask);
        if (warm) await AssertSourceStillAvailableAsync(test.Source);
        var observer = Observer(test.Source);
        observer.IsCompleted.Should().BeFalse();
        test.Source.IsDisposalComplete.Should().BeFalse();
        test.DisposalCalls.Should().Be(0);

        await test.Source.DisposeAsync();
        await observer.WaitAsync(TestBudget);

        test.Source.IsDisposalComplete.Should().BeTrue();
        test.Source.IsValueCreated.Should().Be(warm);
        test.DisposalCalls.Should().Be(warm ? 1 : 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Synthetic_faulted_or_cancelled_witness_is_observed_but_never_releases_the_source(bool cancelled)
    {
        await using var test = new TestSource();
        var witness = cancelled
            ? Task.FromCanceled(new CancellationToken(canceled: true))
            : Task.FromException(new InvalidOperationException("synthetic incomplete ownership"));
        test.Source.RetainBackgroundJobsUntil(witness);
        var observer = Observer(test.Source);
        test.Source.Dispose();
        await observer.WaitAsync(TestBudget);

        test.Source.HasUnconfirmedCleanup.Should().BeTrue();
        test.Source.IsDisposalComplete.Should().BeFalse();
        await AssertSourceStillAvailableAsync(test.Source);
        test.Source.Dispose();
        await test.Source.DisposeAsync();
        Observer(test.Source).Should().BeSameAs(observer);
        test.DisposalCalls.Should().Be(0);
        test.Source.IsDisposalComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Registration_is_one_shot_and_cannot_follow_even_cold_disposal()
    {
        await using var test = new TestSource();
        var positive = Signal();
        test.Source.RetainBackgroundJobsUntil(positive.Task);
        var originalObserver = Observer(test.Source);
        var replace = () => test.Source.RetainBackgroundJobsUntil(Task.CompletedTask);
        replace.Should().Throw<InvalidOperationException>().WithMessage("*already bound*");
        Observer(test.Source).Should().BeSameAs(originalObserver);
        positive.SetResult();
        test.Source.Dispose();
        await originalObserver.WaitAsync(TestBudget);

        using var disposed = new PrivilegedDataSource(() => throw new InvalidOperationException("must stay lazy"));
        disposed.Dispose();
        var late = () => disposed.RetainBackgroundJobsUntil(Task.CompletedTask);
        late.Should().Throw<ObjectDisposedException>();
        disposed.IsValueCreated.Should().BeFalse();
        disposed.IsDisposalComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Concurrent_positive_completion_and_disposal_select_one_actual_disposal()
    {
        await using var test = new TestSource();
        await AssertSourceStillAvailableAsync(test.Source);
        var positive = Signal();
        var start = Signal();
        test.Source.RetainBackgroundJobsUntil(positive.Task);
        var observer = Observer(test.Source);
        var completion = Task.Run(async () =>
        {
            await start.Task;
            positive.SetResult();
        });
        var synchronous = Task.Run(async () =>
        {
            await start.Task;
            test.Source.Dispose();
        });
        var asynchronous = Task.Run(async () =>
        {
            await start.Task;
            await test.Source.DisposeAsync();
        });

        start.SetResult();
        await Task.WhenAll(completion, synchronous, asynchronous).WaitAsync(TestBudget);
        await observer.WaitAsync(TestBudget);
        test.DisposalCalls.Should().Be(1);
        test.Source.IsDisposalComplete.Should().BeTrue();
        Observer(test.Source).Should().BeSameAs(observer);
    }

    [Fact]
    public async Task Registration_racing_disposal_either_retains_or_refuses_before_a_pool_exists()
    {
        await using var test = new TestSource();
        var positive = Signal();
        var start = Signal();
        var registered = false;
        var registration = Task.Run(async () =>
        {
            await start.Task;
            try
            {
                test.Source.RetainBackgroundJobsUntil(positive.Task);
                registered = true;
            }
            catch (ObjectDisposedException) { }
        });
        var disposal = Task.Run(async () =>
        {
            await start.Task;
            test.Source.Dispose();
        });

        start.SetResult();
        await Task.WhenAll(registration, disposal).WaitAsync(TestBudget);
        test.Source.IsValueCreated.Should().BeFalse();
        test.DisposalCalls.Should().Be(0);
        if (registered)
        {
            test.Source.IsDisposalComplete.Should().BeFalse();
            positive.SetResult();
            await Observer(test.Source).WaitAsync(TestBudget);
        }
        test.Source.IsDisposalComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Synthetic_held_actual_source_disposal_remains_owned_after_repeated_container_requests()
    {
        var entered = Signal();
        var release = Signal();
        await using var test = new TestSource(() =>
        {
            entered.SetResult();
            release.Task.GetAwaiter().GetResult();
        });
        await AssertSourceStillAvailableAsync(test.Source);
        test.Source.RetainBackgroundJobsUntil(Task.CompletedTask);
        var observer = Observer(test.Source);
        try
        {
            test.Source.Dispose();
            await entered.Task.WaitAsync(TestBudget);
            observer.IsCompleted.Should().BeFalse();
            test.Source.IsDisposalComplete.Should().BeFalse();
            test.Source.Dispose();
            await test.Source.DisposeAsync();
            test.DisposalCalls.Should().Be(1);
            Observer(test.Source).Should().BeSameAs(observer);
        }
        finally
        {
            release.TrySetResult();
            await observer.WaitAsync(TestBudget);
        }
        test.Source.IsDisposalComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Synthetic_actual_disposal_fault_is_retained_without_retry_or_false_confirmation()
    {
        await using var test = new TestSource(failCleanup: true);
        await AssertSourceStillAvailableAsync(test.Source);
        test.Source.RetainBackgroundJobsUntil(Task.CompletedTask);
        var observer = Observer(test.Source);
        test.Source.Dispose();
        await observer.WaitAsync(TestBudget);

        test.DisposalCalls.Should().Be(1);
        test.Source.HasUnconfirmedCleanup.Should().BeTrue();
        test.Source.IsDisposalComplete.Should().BeFalse();
        typeof(PrivilegedDataSource).GetField("_disposalFailure", PrivateInstance)!
            .GetValue(test.Source).Should().BeOfType<AggregateException>();
        test.Source.Dispose();
        await test.Source.DisposeAsync();
        Observer(test.Source).Should().BeSameAs(observer);
        test.DisposalCalls.Should().Be(1);
        test.Source.IsDisposalComplete.Should().BeFalse("the driver's next Dispose would be an idempotent no-op");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unpinned_synthetic_disposal_fault_preserves_the_callers_exception_without_claiming_cleanup(bool asyncDisposal)
    {
        await using var test = new TestSource(failCleanup: true);
        await AssertSourceStillAvailableAsync(test.Source);
        var disposal = () => RequestDisposalAsync(test.Source, asyncDisposal);
        await disposal.Should().ThrowAsync<AggregateException>();
        test.Source.HasUnconfirmedCleanup.Should().BeTrue();
        test.Source.IsDisposalComplete.Should().BeFalse();
        test.Source.Dispose();
        await test.Source.DisposeAsync();
        test.DisposalCalls.Should().Be(1);
        test.Source.IsDisposalComplete.Should().BeFalse();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Observer(PrivilegedDataSource source) =>
        (Task)typeof(PrivilegedDataSource).GetField("_retentionObserver", PrivateInstance)!.GetValue(source)!;

    private static async Task AssertSourceStillAvailableAsync(PrivilegedDataSource source)
    {
        // An already-cancelled open exercises the real guarded/lazy acquisition
        // path without initiating database I/O; disposal would instead refuse it.
        var open = async () => await source.OpenConnectionAsync(new CancellationToken(canceled: true));
        await open.Should().ThrowAsync<OperationCanceledException>();
    }

    private static async Task RequestDisposalAsync(PrivilegedDataSource source, bool asyncDisposal)
    {
        if (asyncDisposal) await source.DisposeAsync();
        else source.Dispose();
    }

    /// <summary>
    /// Synthetic controls plant only Npgsql's private password-refresh cancellation
    /// owner. They qualify our lifetime state, not production fault incidence.
    /// </summary>
    private sealed class TestSource : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _raw = NpgsqlDataSource.Create("Host=localhost;Port=1;Database=learnstack");
        private readonly CancellationTokenSource _cleanup = new();
        private readonly CancellationTokenRegistration _registration;
        private bool _cleanupFailed;
        private int _disposalCalls;

        internal TestSource(Action? duringCleanup = null, bool failCleanup = false)
        {
            _registration = _cleanup.Token.Register(() =>
            {
                Interlocked.Increment(ref _disposalCalls);
                duringCleanup?.Invoke();
                if (!failCleanup) return;
                _cleanupFailed = true;
                throw new InvalidOperationException("synthetic Npgsql source cleanup failure");
            });
            typeof(NpgsqlDataSource).GetField("_timerPasswordProviderCancellationTokenSource", PrivateInstance)!
                .SetValue(_raw, _cleanup);
            Source = new PrivilegedDataSource(() => _raw);
        }

        internal PrivilegedDataSource Source { get; }
        internal int DisposalCalls => Volatile.Read(ref _disposalCalls);

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            var positive = (Task?)typeof(PrivilegedDataSource).GetField("_positiveCompletion", PrivateInstance)!.GetValue(Source);
            if (positive?.IsCompleted == true) await Observer(Source).WaitAsync(TestBudget);
            // Undo the synthetic fault only for test cleanup. Production never
            // resets the driver's pre-disposal flag or retries uncertain disposal.
            typeof(NpgsqlDataSource).GetField("_timerPasswordProviderCancellationTokenSource", PrivateInstance)!
                .SetValue(_raw, null);
            if (_cleanupFailed)
                typeof(NpgsqlDataSource).GetField("_isDisposed", PrivateInstance)!.SetValue(_raw, 0);
            await _raw.DisposeAsync();
            _registration.Dispose();
            _cleanup.Dispose();
        }
    }
}
