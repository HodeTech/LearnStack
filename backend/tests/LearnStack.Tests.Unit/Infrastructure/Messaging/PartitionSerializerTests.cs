using System.Collections.Concurrent;
using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Messaging;

/// <summary>
/// Per-partition ordering: sequential within one key, concurrent across keys.
/// </summary>
public sealed class PartitionSerializerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Work_On_One_Key_Runs_In_Order_And_Never_Overlaps()
    {
        var serializer = new PartitionSerializer();
        var observed = new ConcurrentQueue<int>();
        var inFlight = 0;
        var overlapped = false;

        var queued = Enumerable.Range(0, 25).Select(i =>
            serializer.RunSequentiallyFor("k", async () =>
            {
                if (Interlocked.Increment(ref inFlight) > 1)
                {
                    Volatile.Write(ref overlapped, true);
                }

                await Task.Yield();
                observed.Enqueue(i);
                Interlocked.Decrement(ref inFlight);
            })).ToArray();

        await Task.WhenAll(queued).WaitAsync(Timeout);

        overlapped.Should().BeFalse();
        observed.Should().BeEquivalentTo(Enumerable.Range(0, 25), o => o.WithStrictOrdering());
    }

    [Fact]
    public async Task Different_Keys_Do_Not_Wait_For_Each_Other()
    {
        // Serialising everything would satisfy the ordering test and defeat the
        // purpose, so the guarantee is pinned from both sides.
        var serializer = new PartitionSerializer();
        using var arrived = new SemaphoreSlim(0);

        async Task Rendezvous()
        {
            arrived.Release();
            await arrived.WaitAsync(Timeout, CancellationToken.None);
        }

        var first = serializer.RunSequentiallyFor("k1", Rendezvous);
        var second = serializer.RunSequentiallyFor("k2", Rendezvous);

        // Each releases once and waits for the other: if the two keys shared a
        // chain the first would wait forever.
        await Task.WhenAll(first, second).WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_Failure_Belongs_To_Its_Own_Unit()
    {
        var serializer = new PartitionSerializer();
        var ran = false;

        var failing = serializer.RunSequentiallyFor("k", () =>
            throw new InvalidOperationException("unit failed"));

        await ((Func<Task>)(() => failing)).Should().ThrowAsync<InvalidOperationException>();

        await serializer.RunSequentiallyFor("k", () =>
        {
            ran = true;
            return Task.CompletedTask;
        }).WaitAsync(Timeout);

        ran.Should().BeTrue("a failed unit must not stop the rest of its partition");
    }

    [Fact]
    public async Task A_Failure_Does_Not_Fault_The_Units_Queued_Behind_It()
    {
        // The chain is built on a copy that swallows the fault, so a later unit
        // does not inherit an earlier one's exception.
        var serializer = new PartitionSerializer();
        using var hold = new SemaphoreSlim(0);

        var failing = serializer.RunSequentiallyFor("k", async () =>
        {
            await hold.WaitAsync(Timeout, CancellationToken.None);
            throw new InvalidOperationException("unit failed");
        });

        var behind = serializer.RunSequentiallyFor("k", () => Task.CompletedTask);

        hold.Release();
        await ((Func<Task>)(() => failing)).Should().ThrowAsync<InvalidOperationException>();

        var act = () => behind.WaitAsync(Timeout);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task The_Map_Holds_One_Entry_Per_In_Flight_Key_Not_Per_Key_Ever_Seen()
    {
        // A structure that grew with the key space would be the same defect the
        // cache's ceiling exists to prevent, in a component nobody thinks to
        // look at — and partition keys are aggregate ids, so the key space is
        // exactly as unbounded as the data.
        var serializer = new PartitionSerializer();

        for (var i = 0; i < 5_000; i++)
        {
            await serializer.RunSequentiallyFor($"k{i}", () => Task.CompletedTask)
                .WaitAsync(Timeout);
        }

        serializer.TrackedPartitions.Should().Be(0, "nothing is in flight any more");
    }

    [Fact]
    public async Task A_Chain_Is_Not_Dropped_While_Work_Is_Still_Queued_Behind_It()
    {
        // Retiring by key alone would drop a chain whose FIRST unit finished
        // while a later one is still running: the next arrival would then start
        // from nothing and run concurrently with work already in flight — the
        // ordering break this class exists to prevent, introduced by its own
        // cleanup.
        var serializer = new PartitionSerializer();
        using var releaseFirst = new SemaphoreSlim(0);
        using var releaseSecond = new SemaphoreSlim(0);

        var first = serializer.RunSequentiallyFor("k", () => releaseFirst.WaitAsync(Timeout));
        var second = serializer.RunSequentiallyFor("k", () => releaseSecond.WaitAsync(Timeout));

        releaseFirst.Release();
        await first.WaitAsync(Timeout);

        // The first unit is done and its retirement has had every chance to run;
        // the second is still in flight.
        var third = serializer.RunSequentiallyFor("k", () => Task.CompletedTask);
        await Task.Delay(TimeSpan.FromMilliseconds(100));

        third.IsCompleted.Should().BeFalse(
            "the third unit waits behind the second, which has not finished");

        releaseSecond.Release();
        await Task.WhenAll(second, third).WaitAsync(Timeout);
    }

    [Fact]
    public async Task A_Failing_Unit_Nobody_Awaits_Leaves_No_Unobserved_Exception()
    {
        // A publisher is free not to await what RunSequentiallyFor returns.
        const string Sentinel = "learnstack-partition-unobserved-probe";
        var mine = new ConcurrentBag<Exception>();

        void Handler(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            if (e.Exception.Flatten().InnerExceptions.Any(inner => inner.Message == Sentinel))
            {
                mine.Add(e.Exception);
                e.SetObserved();
            }
        }

        TaskScheduler.UnobservedTaskException += Handler;
        try
        {
            for (var round = 0; round < 10; round++)
            {
                var serializer = new PartitionSerializer();
                _ = serializer.RunSequentiallyFor("k", () =>
                    Task.FromException(new InvalidOperationException(Sentinel)));
                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }

            for (var collection = 0; collection < 4; collection++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(300));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            mine.Should().BeEmpty("the chain observes the fault it swallows");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= Handler;
        }
    }

    [Fact]
    public async Task An_In_Flight_Key_Is_Still_Tracked()
    {
        // The other side of the same claim: retiring eagerly would drop a chain
        // that still has work behind it, and the next unit would start from
        // scratch — running concurrently with work already in flight.
        var serializer = new PartitionSerializer();
        using var hold = new SemaphoreSlim(0);

        var running = serializer.RunSequentiallyFor("k", () => hold.WaitAsync(Timeout));

        serializer.TrackedPartitions.Should().Be(1);

        hold.Release();
        await running.WaitAsync(Timeout);
    }
}
