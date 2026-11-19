using System.Diagnostics;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The freeze of ADR-0104 § 5 against real PostgreSQL: authority qualification and the User-creation admission
/// take the shared form of the rebind lock, wait for a running rebind and then read the value it left.
/// </summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentityAuthorityRebindLockTests(PublicReadFixture parent)
{
    private const string OldIssuer = "https://old.example.test/realms/learnstack";
    private const string NewIssuer = "https://new.example.test/realms/learnstack";

    [Fact]
    public async Task A_start_that_overlaps_a_rebind_waits_for_it_and_then_fails_on_the_new_issuer()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var qualification = proof.Qualification(proof.Old);
        await using var rebind = await proof.BeginRebindAsync();

        var start = qualification.QualifyAsync();
        await proof.WaitForSharedWaiterAsync();
        start.IsCompleted.Should().BeFalse("the start waits for the lock and does not read the old value");

        await rebind.ChangeIssuerAsync(NewIssuer);
        await rebind.CommitAsync();

        var act = async () => await start;
        (await act.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("Identity authority startup qualification is unavailable or does not match deployment configuration.");
        qualification.Current.Should().BeNull("a failed qualification stays failed until the process restarts");

        await proof.Qualification(proof.Renamed).QualifyAsync();
    }

    [Fact]
    public async Task A_start_that_overlaps_a_rebind_that_rolls_back_qualifies_against_the_unchanged_issuer()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var qualification = proof.Qualification(proof.Old);
        await using var rebind = await proof.BeginRebindAsync();
        await rebind.ChangeIssuerAsync(NewIssuer);

        var start = qualification.QualifyAsync();
        await proof.WaitForSharedWaiterAsync();
        await rebind.RollbackAsync();

        await start;
        qualification.Current.Should().Be(proof.Old);
    }

    [Fact]
    public async Task A_start_that_follows_a_committed_rebind_with_the_old_configuration_is_refused_without_waiting()
    {
        await using var proof = await Proof.CreateAsync(parent);
        await using (var rebind = await proof.BeginRebindAsync())
        {
            await rebind.ChangeIssuerAsync(NewIssuer);
            await rebind.CommitAsync();
        }

        var act = async () => await proof.Qualification(proof.Old).QualifyAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
        await proof.Qualification(proof.Renamed).QualifyAsync();
    }

    [Fact]
    public async Task A_creation_during_a_rebind_is_refused_within_its_lock_timeout_and_binds_nobody()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var person = UserId.From(Guid.CreateVersion7());
        var subject = "locked-" + person.Value.ToString("N");
        RebindSession? rebind = null;
        try
        {
            var timing = Stopwatch.StartNew();
            var result = await proof.Database.TryAdoptAsync(proof.Old, person, subject, async () =>
            {
                rebind = await proof.BeginRebindAsync();
                timing.Restart();
            });
            timing.Stop();

            result.IsBound.Should().BeFalse("the creation owner's 2 s lock timeout refuses it while the rebind runs");
            timing.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
        }
        finally
        {
            if (rebind is not null) await rebind.DisposeAsync();
        }

        (await proof.CountUsersAsync()).Should().Be(2, "no user was created");
    }

    [Fact]
    public async Task A_process_that_qualified_before_a_rebind_cannot_create_a_user_after_it()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var person = UserId.From(Guid.CreateVersion7());

        var result = await proof.Database.TryAdoptAsync(proof.Old, person, "straggler-" + person.Value.ToString("N"), async () =>
        {
            await using var rebind = await proof.BeginRebindAsync();
            await rebind.ChangeIssuerAsync(NewIssuer);
            await rebind.CommitAsync();
        });

        result.IsBound.Should().BeFalse("it would otherwise create a person from the old realm under the new issuer");
        (await proof.CountUsersAsync()).Should().Be(2);
    }

    [Fact]
    public async Task A_process_started_after_a_rebind_creates_users_under_the_new_issuer_with_the_same_authority()
    {
        await using var proof = await Proof.CreateAsync(parent);
        await using (var rebind = await proof.BeginRebindAsync())
        {
            await rebind.ChangeIssuerAsync(NewIssuer);
            await rebind.CommitAsync();
        }

        var person = UserId.From(Guid.CreateVersion7());
        var result = await proof.Database.TryAdoptAsync(proof.Renamed, person, "after-" + person.Value.ToString("N"));

        result.IsBound.Should().BeTrue();
        result.Actor.Should().Be(person);
        (await proof.CountUsersAsync()).Should().Be(3);
    }

    private sealed class Proof : IAsyncDisposable
    {
        private readonly List<NpgsqlDataSource> _sources = [];

        private Proof(IdentityPublicReadFixture database)
        {
            Database = database;
            Old = database.Registration;
            Renamed = new IdentityAuthorityRegistration(Old.AuthorityId, NewIssuer);
        }

        internal IdentityPublicReadFixture Database { get; }

        internal IdentityAuthorityRegistration Old { get; }

        /// <summary>The same stable authority under the new issuer, which is what the deployment configures afterwards.</summary>
        internal IdentityAuthorityRegistration Renamed { get; }

        internal static async Task<Proof> CreateAsync(PublicReadFixture parent)
        {
            var first = UserId.From(Guid.CreateVersion7());
            var second = UserId.From(Guid.CreateVersion7());
            var database = await IdentityPublicReadFixture.CreateAsync(
                parent, OldIssuer,
                new Dictionary<UserId, string>
                {
                    [first] = "lock-proof-" + first.Value.ToString("N"),
                    [second] = "lock-proof-" + second.Value.ToString("N"),
                });
            return new Proof(database);
        }

        internal IdentityAuthorityQualification Qualification(IdentityAuthorityRegistration registration)
        {
            var source = NpgsqlDataSource.Create(Database.AppConnectionString);
            _sources.Add(source);
            return new IdentityAuthorityQualification(new Lazy<NpgsqlDataSource>(() => source), registration);
        }

        internal async Task<RebindSession> BeginRebindAsync()
        {
            var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.IdentityRebindConnectionString);
            var transaction = await connection.BeginTransactionAsync();
            var session = new RebindSession(connection, transaction);
            await session.TakeLockAsync();
            return session;
        }

        /// <summary>Observed from outside: a start's shared request is queued behind the exclusive holder.</summary>
        internal async Task WaitForSharedWaiterAsync()
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.MigrationConnectionString);
            var key = IdentityAdvisoryLockKey.ForAuthorityRebind();
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                await using var command = new NpgsqlCommand(
                    """
                    SELECT count(*) FROM pg_catalog.pg_locks
                    WHERE locktype = 'advisory' AND NOT granted AND mode = 'ShareLock' AND objsubid = 1
                      AND ((classid::bigint << 32) | objid::bigint) = @key
                    """, connection);
                command.Parameters.AddWithValue("key", key);
                if ((long)(await command.ExecuteScalarAsync())! > 0) return;
                await Task.Delay(50);
            }

            throw new TimeoutException("No shared waiter queued behind the rebind lock.");
        }

        internal async Task<long> CountUsersAsync()
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.IdentityRebindConnectionString);
            await using var command = new NpgsqlCommand("SELECT count(*) FROM public.users", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var source in _sources) await source.DisposeAsync();
            await Database.DisposeAsync();
        }
    }

    private sealed class RebindSession(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
    {
        private bool _finished;

        internal async Task TakeLockAsync()
        {
            await using var command = new NpgsqlCommand("SELECT pg_catalog.pg_advisory_xact_lock(@key)", connection, transaction);
            command.Parameters.AddWithValue("key", IdentityAdvisoryLockKey.ForAuthorityRebind());
            await command.ExecuteNonQueryAsync();
        }

        internal async Task ChangeIssuerAsync(string issuer)
        {
            await using var command = new NpgsqlCommand(
                "UPDATE public.identity_authority SET issuer = @issuer WHERE id = 1", connection, transaction);
            command.Parameters.AddWithValue("issuer", issuer);
            (await command.ExecuteNonQueryAsync()).Should().Be(1);
        }

        internal async Task CommitAsync()
        {
            await transaction.CommitAsync();
            _finished = true;
        }

        internal async Task RollbackAsync()
        {
            await transaction.RollbackAsync();
            _finished = true;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_finished) await transaction.RollbackAsync();
            await transaction.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
