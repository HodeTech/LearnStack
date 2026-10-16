using System.Data;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Messaging;

/// <summary>
/// Synthetic owner controls: reflection plants an invalid driver state on an
/// unopened connection. These prove retention/sealing, not real failure incidence.
/// </summary>
public sealed class PrivilegedDispatchCleanupSealTests
{
    private const BindingFlags InstanceMembers = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Synthetic_connection_disposal_fault_retains_owner_and_seals_later_acquisition(int kind)
    {
        await using var fixture = new Fixture(kind);
        using var connection = new NpgsqlConnection();
        var owned = fixture.PlantOwner(0, connection);
        SetDriverState(connection, ConnectionState.Connecting);
        try
        {
            var cleanup = ((IAsyncDisposable)owned).DisposeAsync().AsTask();
            var failure = async () => await cleanup;
            await failure.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cleanup remains unconfirmed*");
            fixture.HasUnconfirmedCleanup.Should().BeTrue();
            fixture.OwnerAt(0).Should().BeSameAs(owned);
            Field(owned, "_connection").Should().BeSameAs(connection);
            Field(owned, "_cleanup").Should().BeSameAs(cleanup);
            Field(owned, "_cleanupFailure").Should().BeOfType<InvalidOperationException>();

            var next = () => fixture.TryClaimAsync();
            await next.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cleanup remains unconfirmed*");
            fixture.Builds.Should().Be(0, "the cleanup seal must reject before even constructing a source");

            // Repairing this synthetic input must not authorize an application
            // retry of the original uncertain disposal or release its owner slot.
            SetDriverState(connection, ConnectionState.Closed);
            var repeated = ((IAsyncDisposable)owned).DisposeAsync().AsTask();
            repeated.Should().BeSameAs(cleanup);
            await failure.Should().ThrowAsync<InvalidOperationException>();
            fixture.OwnerAt(0).Should().BeSameAs(owned);
            fixture.HasUnconfirmedCleanup.Should().BeTrue();
        }
        finally
        {
            SetDriverState(connection, ConnectionState.Closed);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Synthetic_body_failure_with_successful_connection_disposal_releases_its_slot(int kind)
    {
        await using var fixture = new Fixture(kind);
        using var connection = new NpgsqlConnection();
        var owned = fixture.PlantOwner(0, connection);
        var bodyFailure = new InvalidOperationException("synthetic SQL-body failure");

        var operation = () => FailBodyAsync((IAsyncDisposable)owned, bodyFailure);
        var thrown = await operation.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Should().BeSameAs(bodyFailure);
        fixture.HasUnconfirmedCleanup.Should().BeFalse();
        fixture.OwnerAt(0).Should().BeNull();

        var next = () => fixture.TryClaimAsync();
        await next.Should().ThrowAsync<InvalidOperationException>().WithMessage("synthetic source construction refusal");
        fixture.Builds.Should().Be(1, "a positively released lane remains retryable");
        fixture.OwnerAt(0).Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Synthetic_outer_and_reconciliation_cleanup_failures_retain_both_fixed_slots(int kind)
    {
        await using var fixture = new Fixture(kind);
        using var outerConnection = new NpgsqlConnection();
        using var reconciliationConnection = new NpgsqlConnection();
        var outer = fixture.PlantOwner(0, outerConnection);
        var reconciliation = fixture.PlantOwner(1, reconciliationConnection);
        SetDriverState(outerConnection, ConnectionState.Connecting);
        SetDriverState(reconciliationConnection, ConnectionState.Connecting);
        try
        {
            var nestedCleanup = async () => await ((IAsyncDisposable)reconciliation).DisposeAsync();
            await nestedCleanup.Should().ThrowAsync<InvalidOperationException>();
            var outerCleanup = async () => await ((IAsyncDisposable)outer).DisposeAsync();
            await outerCleanup.Should().ThrowAsync<InvalidOperationException>();

            fixture.OwnerAt(0).Should().BeSameAs(outer);
            fixture.OwnerAt(1).Should().BeSameAs(reconciliation);
            fixture.HasUnconfirmedCleanup.Should().BeTrue();
            var next = () => fixture.TryClaimAsync();
            await next.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cleanup remains unconfirmed*");
            fixture.Builds.Should().Be(0);
        }
        finally
        {
            SetDriverState(outerConnection, ConnectionState.Closed);
            SetDriverState(reconciliationConnection, ConnectionState.Closed);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Fixed_slots_refuse_excess_same_store_depth_before_acquisition(int kind)
    {
        await using var fixture = new Fixture(kind);
        using var firstConnection = new NpgsqlConnection();
        using var secondConnection = new NpgsqlConnection();
        var first = fixture.PlantOwner(0, firstConnection);
        var second = kind == 2 ? null : fixture.PlantOwner(1, secondConnection);
        try
        {
            var next = () => fixture.TryClaimAsync();
            await next.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ownership bound was exceeded*");
            fixture.Builds.Should().Be(0);
            fixture.HasUnconfirmedCleanup.Should().BeFalse();
        }
        finally
        {
            if (second is not null) await ((IAsyncDisposable)second).DisposeAsync();
            await ((IAsyncDisposable)first).DisposeAsync();
        }
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    public async Task Synthetic_cleanup_fault_preserves_an_uncertain_commit_until_positive_reconciliation(int kind, bool confirmed)
    {
        await using var fixture = new Fixture(kind);
        using var connection = new NpgsqlConnection();
        var owned = fixture.PlantOwner(0, connection);
        var commitFailure = new NpgsqlException("synthetic uncertain COMMIT");
        owned.GetType().GetMethod("RecordCommitFailure", InstanceMembers)!.Invoke(owned, [commitFailure]);
        if (confirmed)
            owned.GetType().GetMethod("ConfirmCommitOutcome", InstanceMembers)!.Invoke(owned, null);
        SetDriverState(connection, ConnectionState.Connecting);
        try
        {
            var cleanup = async () => await ((IAsyncDisposable)owned).DisposeAsync();
            if (confirmed)
            {
                await cleanup.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cleanup remains unconfirmed*");
            }
            else
            {
                var thrown = await cleanup.Should().ThrowAsync<AggregateException>()
                    .WithMessage("*terminal commit outcome remains unknown*");
                thrown.Which.InnerExceptions.Should().Contain(commitFailure);
                thrown.Which.InnerExceptions.Should().Contain(exception => exception is InvalidOperationException);
            }
            fixture.HasUnconfirmedCleanup.Should().BeTrue();
        }
        finally
        {
            SetDriverState(connection, ConnectionState.Closed);
        }
    }

    private static async Task FailBodyAsync(IAsyncDisposable owner, Exception failure)
    {
        await using (owner.ConfigureAwait(false))
        {
            throw failure;
        }
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, InstanceMembers)!.GetValue(target);

    private static void SetDriverState(NpgsqlConnection connection, ConnectionState state) =>
        typeof(NpgsqlConnection).GetField("_fullState", InstanceMembers)!.SetValue(connection, state);

    private sealed record CleanupProofEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.test.cleanup";
        public override string PartitionKey => TenantId.ToString("D");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly PrivilegedDataSource _source;
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly object _store;

        internal Fixture(int kind)
        {
            _source = new PrivilegedDataSource(() =>
            {
                Builds++;
                throw new InvalidOperationException("synthetic source construction refusal");
            });
            var registry = OutboxEventRegistry.Create(
                [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(CleanupProofEvent))], [],
                IntegrationEventHandlerRegistry.FromServiceDescriptors(new ServiceCollection()),
                [typeof(CleanupProofEvent).Assembly]);
            var scopes = _services.GetRequiredService<IServiceScopeFactory>();
            _store = kind switch
            {
                0 => new OutboxDispatchStore(_source, registry, new OutboxDispatchOptions(),
                    new SystemGuidFactory(), new SystemClock(), scopes),
                1 => new DeliveryDispatchStore(_source, registry, new DeliveryDispatchOptions(),
                    new SystemGuidFactory(), new SystemClock(), scopes),
                2 => new PostgresBackgroundJobBridgeStore(_source, BackgroundJobDefinitionRegistry.CreateEmpty(),
                    new BackgroundJobRuntimeOptions(), new SystemGuidFactory()),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }

        internal int Builds { get; private set; }

        internal bool HasUnconfirmedCleanup => _store switch
        {
            OutboxDispatchStore store => store.HasUnconfirmedCleanup,
            DeliveryDispatchStore store => store.HasUnconfirmedCleanup,
            PostgresBackgroundJobBridgeStore store => store.HasUnconfirmedCleanup,
            _ => throw new InvalidOperationException("Unexpected test store."),
        };

        internal object PlantOwner(int slot, NpgsqlConnection connection)
        {
            var type = _store.GetType().GetNestedType("OwnedConnection", BindingFlags.NonPublic)!;
            var constructor = type.GetConstructors(InstanceMembers).Single();
            var owned = constructor.Invoke(_store is PostgresBackgroundJobBridgeStore ? [_store] : [_store, slot]);
            type.GetMethod("Attach", InstanceMembers)!.Invoke(owned, [connection]);
            if (_store is PostgresBackgroundJobBridgeStore)
                _store.GetType().GetField("_connection", InstanceMembers)!.SetValue(_store, owned);
            else
                ((Array)Field(_store, "_connections")!).SetValue(owned, slot);
            return owned;
        }

        internal object? OwnerAt(int slot) => _store is PostgresBackgroundJobBridgeStore
            ? Field(_store, "_connection") : ((Array)Field(_store, "_connections")!).GetValue(slot);

        internal Task TryClaimAsync(CancellationToken cancellationToken = default) => _store switch
        {
            OutboxDispatchStore store => store.TryClaimAsync([], cancellationToken),
            DeliveryDispatchStore store => store.TryClaimAsync([], cancellationToken),
            PostgresBackgroundJobBridgeStore store => (Task)store.GetType()
                .GetMethod("TryClaimAsync", InstanceMembers)!
                .Invoke(store, [Array.Empty<BackgroundJobId>(), cancellationToken])!,
            _ => throw new InvalidOperationException("Unexpected test store."),
        };

        public async ValueTask DisposeAsync()
        {
            await _source.DisposeAsync();
            await _services.DisposeAsync();
        }
    }
}
