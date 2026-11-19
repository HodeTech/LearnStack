using System.Data.Common;
using FluentAssertions;
using LearnStack.Modules.Tenancy.Infrastructure;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The order of the killswitch writer's steps and the failure branches around its commit, decided
/// against a scripted scope so a commit that faults can be produced exactly (ADR-0101 § 13).
/// </summary>
/// <remarks>
/// No Docker. The real-database behaviour (row lock, both audit rows, rollback) is asserted in
/// <see cref="PlatformAdminScopeTests"/>; what a stub can show and a database cannot is the writer's
/// reaction to a commit whose outcome is unknown.
/// </remarks>
public sealed class KillswitchToggleOrderingTests
{
    private static readonly KillswitchKey Key = KillswitchKeys.AnalyticsIngestEnabled;

    [Fact]
    public async Task A_Commit_Whose_Outcome_Is_Unknown_Still_Drops_The_Cache_And_Reaches_The_Caller()
    {
        // The flip may have landed. A stale overlay entry must not outlive a commit that might have
        // happened, and the caller must see the failure rather than an Applied it cannot trust.
        var handle = new ScriptedHandle { CommitFailure = new TimeoutException("the commit reply never came") };
        var cache = new CountingCache();

        var act = async () => await Toggle(handle, cache).ToggleAsync(TestPlatformAdmissions.Admission(), Key, false);

        await act.Should().ThrowAsync<TimeoutException>();
        cache.Removed.Should().Be(1);
        handle.Disposed.Should().BeTrue("the scope is released whatever the commit did");
    }

    [Fact]
    public async Task A_Failure_Before_The_Commit_Leaves_The_Cache_Alone()
    {
        var handle = new ScriptedHandle { ResultFailure = new LearnStack.SharedKernel.Audit.AuditWriteFailedException("no row") };
        var cache = new CountingCache();

        var act = async () => await Toggle(handle, cache).ToggleAsync(TestPlatformAdmissions.Admission(), Key, false);

        await act.Should().ThrowAsync<LearnStack.SharedKernel.Audit.AuditWriteFailedException>();
        cache.Removed.Should().Be(0, "nothing was committed, so nothing is stale");
        handle.CommitCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_Commit_Is_Not_Cancellable_By_The_Caller()
    {
        // Past the point where the caller's abort could matter, a cancelled commit would leave its
        // outcome unknown, which is the state this order exists to avoid.
        var handle = new ScriptedHandle();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        await Toggle(handle, new CountingCache()).ToggleAsync(TestPlatformAdmissions.Admission(), Key, false, abort.Token);

        handle.CommitToken.CanBeCanceled.Should().BeFalse();
    }

    [Fact]
    public async Task A_Cache_That_Cannot_Remove_Is_Logged_And_Does_Not_Fail_The_Applied_Toggle()
    {
        var handle = new ScriptedHandle();
        var logger = new CapturingLogger<KillswitchToggle>();
        var cache = new CountingCache { Failure = new TimeoutException("cache down") };
        var toggle = new KillswitchToggle(new ScriptedScope(handle), cache, new SystemClock(), logger);

        var result = await toggle.ToggleAsync(TestPlatformAdmissions.Admission(), Key, false);

        result.Outcome.Should().Be(KillswitchToggleOutcome.Applied, "the flip committed; the cache is an optimisation");
        logger.EventIds.Should().Contain(7104, "a store that cannot remove is a store that serves stale");
    }

    [Fact]
    public async Task The_Writer_Creates_Locks_Reads_Updates_Records_And_Only_Then_Commits_In_That_Order()
    {
        var handle = new ScriptedHandle();

        await Toggle(handle, new CountingCache()).ToggleAsync(TestPlatformAdmissions.Admission(ticket: "INC-3"), Key, false);

        handle.Steps.Should().Equal("exec:INSERT", "query:SELECT", "exec:UPDATE", "result", "commit");
        handle.Statements[1].Should().EndWith("FOR UPDATE", "the read takes the row lock the update then holds");
    }

    private static KillswitchToggle Toggle(ScriptedHandle handle, CountingCache cache) =>
        new(new ScriptedScope(handle), cache, new SystemClock(), NullLogger<KillswitchToggle>.Instance);

    private sealed class ScriptedScope(ScriptedHandle handle) : IPlatformAdminScope
    {
        public Task<IPlatformAdminScopeHandle> EnterAsync(
            PlatformAdmission admission, CancellationToken cancellationToken = default,
            string? callerMember = null, string? callerFile = null, int callerLine = 0) =>
            Task.FromResult<IPlatformAdminScopeHandle>(handle);
    }

    private sealed class ScriptedHandle : IPlatformAdminScopeHandle
    {
        public Exception? CommitFailure { get; init; }
        public Exception? ResultFailure { get; init; }
        public List<string> Steps { get; } = [];
        public List<string> Statements { get; } = [];
        public int CommitCalls { get; private set; }
        public CancellationToken CommitToken { get; private set; }
        public bool Disposed { get; private set; }

        public Task<int> ExecuteAsync(
            string sql, IReadOnlyList<PlatformSqlParameter>? parameters = null, CancellationToken cancellationToken = default)
        {
            Steps.Add("exec:" + sql.Split(' ')[0]);
            Statements.Add(sql);
            return Task.FromResult(1);
        }

        public Task<IReadOnlyList<T>> QueryAsync<T>(
            string sql, IReadOnlyList<PlatformSqlParameter>? parameters, Func<DbDataReader, T> map,
            CancellationToken cancellationToken = default)
        {
            Steps.Add("query:" + sql.Split(' ')[0]);
            Statements.Add(sql);
            return Task.FromResult<IReadOnlyList<T>>((IReadOnlyList<T>)(object)new List<bool> { true });
        }

        public Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default)
        {
            Steps.Add("result");
            return ResultFailure is null ? Task.CompletedTask : Task.FromException(ResultFailure);
        }

        public Task CommitAsync(CancellationToken cancellationToken = default)
        {
            Steps.Add("commit");
            CommitCalls++;
            CommitToken = cancellationToken;
            return CommitFailure is null ? Task.CompletedTask : Task.FromException(CommitFailure);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingCache : ICacheService
    {
        public int Removed { get; private set; }
        public Exception? Failure { get; init; }

        public Task RemoveAsync(string key, CancellationToken cancellationToken = default)
        {
            Removed++;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<T> GetOrSetAsync<T>(
            string key, Func<CancellationToken, Task<T>> factory, CacheOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetAsync<T>(string key, T value, CacheOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<int> EventIds { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            EventIds.Add(eventId.Id);
    }
}
