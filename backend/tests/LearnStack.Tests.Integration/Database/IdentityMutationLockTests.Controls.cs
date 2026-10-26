using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMutationLockTests
{
    [Fact]
    public async Task Actual_helper_waits_for_the_user_owner_and_a_separate_postwait_statement_observes_its_commit()
    {
        await using var source = ApplicationDataSource.Build(_schema.Postgres.AppConnectionString);
        await using var owner = Unit(source);
        await using var ownerFrame = await owner.BeginTransactionAsync();
        await owner.SetProvisioningTenantContextAsync(Tenant);
        await new IdentityMutationLocks(owner).AcquireAsync(new([User], Tenant, IdentityTenantLockMode.Shared));
        var ownerXid = await ValueAsync<string>(owner, "SELECT pg_current_xact_id()::text");
        await using var waiter = Unit(source);
        await using var waiterFrame = await waiter.BeginTransactionAsync();
        await waiter.SetProvisioningTenantContextAsync(Tenant);
        (await ValueAsync<bool>(waiter, "SELECT pg_visible_in_snapshot(@xid::xid8, pg_current_snapshot())", ownerXid))
            .Should().BeFalse("the competing transaction is actually in progress in the pre-wait snapshot");
        var plan = new IdentityMutationLockPlan([User], Tenant, IdentityTenantLockMode.Shared);
        var helper = new IdentityMutationLocks(waiter);
        var waiting = helper.AcquireAsync(plan);
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(observer);
        await ObserveWaitAsync(observer, ((NpgsqlConnection)waiter.Connection).ProcessID, IdentityAdvisoryLockKey.ForUser(User));
        waiting.IsCompleted.Should().BeFalse();
        (await ModeCountAsync(observer, ((NpgsqlConnection)waiter.Connection).ProcessID,
            IdentityAdvisoryLockKey.ForTenant(Tenant), true)).Should().Be(0,
                "the tenant family cannot be acquired beneath a pending User acquisition");
        await ownerFrame.CompleteAsync();
        await waiting;
        helper.RequireCurrent(plan);
        (await ValueAsync<bool>(waiter, "SELECT pg_visible_in_snapshot(@xid::xid8, pg_current_snapshot())", ownerXid))
            .Should().BeTrue("a separate READ COMMITTED statement observes the actual competing COMMIT");
        (await HeldAsync(waiter, IdentityAdvisoryLockKey.ForUser(User))).Should().Equal("ExclusiveLock");
        await waiterFrame.FailAsync();
        (await HeldAsync(waiter, IdentityAdvisoryLockKey.ForUser(User))).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Waiting_helper_timeout_or_cancellation_rolls_back_releases_and_reuses_the_physical_pool_connection(bool cancel)
    {
        await using var holderSource = ApplicationDataSource.Build(_schema.Postgres.AppConnectionString);
        await using var holder = Unit(holderSource);
        await using var holderFrame = await holder.BeginTransactionAsync();
        await holder.SetProvisioningTenantContextAsync(Tenant);
        await new IdentityMutationLocks(holder).AcquireAsync(new([User], Tenant, IdentityTenantLockMode.Shared));
        var connectionString = new NpgsqlConnectionStringBuilder(_schema.Postgres.AppConnectionString)
        { MaxPoolSize = 1, MinPoolSize = 0, Pooling = true }.ConnectionString;
        await using var waiterSource = ApplicationDataSource.Build(connectionString);
        int processId;
        using var signals = new LockSignals();
        using var cancellation = new CancellationTokenSource();
        await using (var waiter = Unit(waiterSource))
        {
            await using var frame = await waiter.BeginTransactionAsync();
            await waiter.SetProvisioningTenantContextAsync(Tenant);
            processId = ((NpgsqlConnection)waiter.Connection).ProcessID;
            var helper = new IdentityMutationLocks(waiter);
            var plan = new IdentityMutationLockPlan([User], Tenant, IdentityTenantLockMode.Shared);
            var started = Stopwatch.StartNew();
            var waiting = helper.AcquireAsync(plan, cancellation.Token);
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
            await ObserveWaitAsync(observer, processId, IdentityAdvisoryLockKey.ForUser(User));
            waiting.IsCompleted.Should().BeFalse();
            if (cancel)
            {
                cancellation.Cancel();
                await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<OperationCanceledException>();
            }
            else
            {
                await FluentActions.Awaiting(() => waiting).Should().ThrowAsync<IdentityMutationLockException>();
                started.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5));
            }
            started.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(8));
            waiter.IsRollbackOnly.Should().BeTrue();
            await frame.FailAsync();
            waiter.HasActiveTransaction.Should().BeFalse();
            (await HeldAsync(waiter, IdentityAdvisoryLockKey.ForUser(User))).Should().BeEmpty();
            (await HeldAsync(waiter, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().BeEmpty();
            signals.AssertOneFailure("user", cancel ? "cancelled" : "timeout");
        }
        await holderFrame.CompleteAsync();
        await using var reused = Unit(waiterSource);
        await using var reusedFrame = await reused.BeginTransactionAsync();
        ((NpgsqlConnection)reused.Connection).ProcessID.Should().Be(processId,
            "the one-connection pool must reuse the actual cleaned backend rather than prove a fresh socket is clean");
        (await ValueAsync<bool>(reused, """
            SELECT NULLIF(current_setting('app.tenant_id',true),'') IS NULL
                AND NULLIF(current_setting('app.organization_id',true),'') IS NULL
                AND NULLIF(current_setting('app.identity_authority_id',true),'') IS NULL
            """)).Should().BeTrue();
        (await HeldAsync(reused, IdentityAdvisoryLockKey.ForUser(User))).Should().BeEmpty();
        await reused.SetProvisioningTenantContextAsync(Tenant);
        var fresh = new IdentityMutationLocks(reused);
        var freshPlan = new IdentityMutationLockPlan([User], Tenant, IdentityTenantLockMode.Shared);
        await fresh.AcquireAsync(freshPlan);
        fresh.RequireCurrent(freshPlan);
        await reusedFrame.CompleteAsync();
        (await HeldAsync(reused, IdentityAdvisoryLockKey.ForUser(User))).Should().BeEmpty();
    }

    [Fact]
    public async Task Separate_shared_helpers_coexist_exclusive_waits_and_repeat_upgrade_refusals_emit_one_fixed_fault()
    {
        await using var source = ApplicationDataSource.Build(_schema.Postgres.AppConnectionString);
        await using var first = Unit(source);
        await using var firstFrame = await first.BeginTransactionAsync();
        await first.SetProvisioningTenantContextAsync(Tenant);
        var firstHelper = new IdentityMutationLocks(first);
        var firstPlan = new IdentityMutationLockPlan([], Tenant, IdentityTenantLockMode.Shared);
        await firstHelper.AcquireAsync(firstPlan);
        await using var second = Unit(source);
        await using var secondFrame = await second.BeginTransactionAsync();
        await second.SetProvisioningTenantContextAsync(Tenant);
        var secondHelper = new IdentityMutationLocks(second);
        await secondHelper.AcquireAsync(new([], Tenant, IdentityTenantLockMode.Shared));
        ((NpgsqlConnection)first.Connection).ProcessID.Should().NotBe(((NpgsqlConnection)second.Connection).ProcessID);
        (await HeldAsync(first, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().Equal("ShareLock");
        (await HeldAsync(second, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().Equal("ShareLock");
        await using var exclusive = Unit(source);
        await using var exclusiveFrame = await exclusive.BeginTransactionAsync();
        await exclusive.SetProvisioningTenantContextAsync(Tenant);
        var exclusiveHelper = new IdentityMutationLocks(exclusive);
        var exclusivePlan = new IdentityMutationLockPlan([], Tenant, IdentityTenantLockMode.Exclusive);
        using var signals = new LockSignals();
        var waiting = exclusiveHelper.AcquireAsync(exclusivePlan);
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(_schema.Postgres.AppConnectionString);
        await ObserveWaitAsync(observer, ((NpgsqlConnection)exclusive.Connection).ProcessID, IdentityAdvisoryLockKey.ForTenant(Tenant));
        for (var attempt = 0; attempt < 2; attempt++)
            await FluentActions.Awaiting(() => firstHelper.AcquireAsync(new([], Tenant, IdentityTenantLockMode.Exclusive)))
                .Should().ThrowAsync<IdentityMutationLockException>();
        signals.AssertOnePlanFault("tenant");
        (await HeldAsync(first, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().Equal("ShareLock");
        await firstFrame.FailAsync();
        await ObserveWaitAsync(observer, ((NpgsqlConnection)exclusive.Connection).ProcessID, IdentityAdvisoryLockKey.ForTenant(Tenant));
        waiting.IsCompleted.Should().BeFalse("the independently held second shared fence still excludes an exclusive owner");
        await secondFrame.CompleteAsync();
        await waiting;
        exclusiveHelper.RequireCurrent(exclusivePlan);
        (await HeldAsync(exclusive, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().Equal("ExclusiveLock");
        signals.AssertAcquired("tenant");
        await exclusiveFrame.CompleteAsync();
        (await HeldAsync(exclusive, IdentityAdvisoryLockKey.ForTenant(Tenant))).Should().BeEmpty();
    }

    [Fact]
    public async Task Actual_global_subject_acquisition_emits_a_positive_finite_measurement_and_releases_on_rollback()
    {
        var subject = new GlobalIdentitySubject(IdentityAuthorityId.From(Guid.Parse("01930000-0000-7000-8000-00000000f001")),
            "https://lock-metrics.invalid/realms/learnstack", "subject-canary-must-not-be-a-metric-tag");
        await using var source = ApplicationDataSource.Build(_schema.Postgres.AppConnectionString);
        await using var unit = Unit(source);
        var descriptor = SubjectSignalMint.Create(subject);
        await using var frame = await unit.BeginGlobalIdentityTransactionAsync(descriptor,
            LearnStack.SharedKernel.Persistence.TransactionMode.ReadWrite);
        using var signals = new LockSignals();
        await unit.AcquireGlobalIdentitySubjectLockAsync(descriptor);
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForSubject(subject))).Should().Equal("ExclusiveLock");
        signals.AssertAcquired("subject");
        signals.Snapshot().SelectMany(signal => signal.Tags.Values).Should().NotContain(subject.Subject);
        await frame.FailAsync();
        (await HeldAsync(unit, IdentityAdvisoryLockKey.ForSubject(subject))).Should().BeEmpty();
    }

    private static async Task<T> ValueAsync<T>(NpgsqlUnitOfWork unit, string sql, string? xid = null)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection,
            (NpgsqlTransaction?)unit.Transaction)
        { CommandTimeout = 10 };
        if (xid is not null) command.Parameters.AddWithValue("xid", xid);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ObserveWaitAsync(NpgsqlConnection observer, int processId, long key)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            if (await ModeCountAsync(observer, processId, key, false) == 1) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The competing helper never entered the exact PostgreSQL advisory wait.");
    }

    private static async Task<long> ModeCountAsync(NpgsqlConnection observer, int processId, long key, bool granted)
    {
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_locks l JOIN pg_catalog.pg_stat_activity a ON a.pid=l.pid
            WHERE l.pid=@pid AND l.database=(SELECT oid FROM pg_catalog.pg_database WHERE datname=current_database())
              AND l.locktype='advisory' AND l.objsubid=1 AND l.granted=@granted
              AND (l.classid::bigint << 32 | l.objid::bigint)=@key
              AND (@granted OR (a.wait_event_type='Lock' AND a.wait_event='advisory'))
            """, observer) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("pid", processId);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("granted", granted);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record LockSignal(string Instrument, double Value, IReadOnlyDictionary<string, string> Tags);

    private sealed class LockSignals : IDisposable
    {
        private static readonly AsyncLocal<LockSignals?> Current = new();
        private readonly LockSignals? _previous = Current.Value;
        private static readonly string[] WaitTagNames = ["lock_kind", "outcome"];
        private static readonly string[] RefusalTagNames = ["lock_kind", "reason"];
        private readonly ConcurrentQueue<LockSignal> _signals = new();
        private readonly MeterListener _listener = new();
        internal LockSignals()
        {
            Current.Value = this;
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == IdentityLockObservation.MeterName)
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                Record(instrument, value, tags));
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Record(instrument, value, tags));
            _listener.Start();
        }

        internal LockSignal[] Snapshot() => _signals.ToArray();
        internal void AssertOneFailure(string kind, string outcome)
        {
            AssertFinite();
            var matching = Snapshot().Where(signal => signal.Tags["lock_kind"] == kind).ToArray();
            matching.Where(signal => signal.Instrument == "learnstack_identity_lock_wait_seconds")
                .Should().ContainSingle().Which.Tags["outcome"].Should().Be(outcome);
            matching.Where(signal => signal.Instrument == "learnstack_identity_lock_refusals_total")
                .Should().ContainSingle().Which.Value.Should().Be(1);
            matching.Single(signal => signal.Instrument == "learnstack_identity_lock_refusals_total")
                .Tags["reason"].Should().Be(outcome);
        }
        internal void AssertOnePlanFault(string kind)
        {
            AssertFinite();
            Snapshot().Where(signal => signal.Instrument == "learnstack_identity_lock_refusals_total"
                && signal.Tags["lock_kind"] == kind).Should().ContainSingle()
                .Which.Tags["reason"].Should().Be("fault");
        }
        internal void AssertAcquired(string kind)
        {
            AssertFinite();
            Snapshot().Where(signal => signal.Instrument == "learnstack_identity_lock_wait_seconds"
                && signal.Tags["lock_kind"] == kind && signal.Tags["outcome"] == "acquired")
                .Should().ContainSingle().Which.Value.Should().BeGreaterThanOrEqualTo(0);
        }
        private void AssertFinite()
        {
            Snapshot().Should().NotBeEmpty("the sink must have received actual positive or failed production measurements");
            foreach (var signal in Snapshot())
            {
                signal.Instrument.Should().BeOneOf("learnstack_identity_lock_wait_seconds", "learnstack_identity_lock_refusals_total");
                signal.Tags.Keys.Should().BeEquivalentTo(signal.Instrument == "learnstack_identity_lock_wait_seconds"
                    ? WaitTagNames : RefusalTagNames);
                signal.Tags["lock_kind"].Should().BeOneOf("subject", "user", "tenant");
                if (signal.Instrument == "learnstack_identity_lock_wait_seconds")
                    signal.Tags["outcome"].Should().BeOneOf("acquired", "timeout", "cancelled", "fault");
                else signal.Tags["reason"].Should().BeOneOf("timeout", "cancelled", "fault");
            }
        }
        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            if (ReferenceEquals(Current.Value, this))
                _signals.Enqueue(new(instrument.Name, value, tags.ToArray().ToDictionary(tag => tag.Key,
                    tag => tag.Value?.ToString() ?? string.Empty, StringComparer.Ordinal)));
        }
        public void Dispose()
        {
            _listener.Dispose();
            Current.Value = _previous;
        }
    }

    private sealed class SubjectSignalEntity;
    private sealed class SubjectSignalContext;
    private sealed class SubjectSignalMint : GlobalIdentityOwnerDescriptor.MintingSource
    {
        internal static GlobalIdentityOwnerDescriptor Create(GlobalIdentitySubject subject) =>
            ForBootstrap(subject, typeof(SubjectSignalContext), typeof(SubjectSignalEntity));
    }
}
