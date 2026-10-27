using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Infrastructure.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class AuthorizationPoolProgressTests(AuthorizationPoolProgressFixture fixture, ITestOutputHelper output)
    : IClassFixture<AuthorizationPoolProgressFixture>
{
    [Fact]
    public async Task Transaction_pooler_reserves_two_four_two_distinct_backends_for_the_same_database_and_app_role()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var held = new RawBackendReservations();
        var pids = new List<int>();
        var database = new NpgsqlConnectionStringBuilder(fixture.DirectConnectionString).Database;
        foreach (var (lane, capacity) in new[]
        {
            ("business", AuthorizationPoolProgressFixture.BusinessCapacity),
            ("authorization", AuthorizationPoolProgressFixture.AuthorizationCapacity),
            ("evidence", AuthorizationPoolProgressFixture.EvidenceCapacity),
        })
        {
            for (var index = 0; index < capacity; index++)
            {
                var (connection, transaction) = await held.OpenAsync(
                    fixture.ConnectionString(true, lane, "reserved-" + lane), deadline.Token);
                await using var command = new NpgsqlCommand("""
                    SELECT pg_backend_pid(), current_database(), current_user,
                           NOT r.rolsuper AND NOT r.rolbypassrls
                    FROM pg_roles AS r WHERE r.rolname = current_user
                    """, connection, transaction);
                await using var row = await command.ExecuteReaderAsync(deadline.Token);
                (await row.ReadAsync(deadline.Token)).Should().BeTrue();
                pids.Add(row.GetInt32(0));
                row.GetString(1).Should().Be(database);
                row.GetString(2).Should().Be("learnstack_app");
                row.GetBoolean(3).Should().BeTrue();
            }
        }
        pids.Should().HaveCount(AuthorizationPoolProgressFixture.BusinessCapacity
            + AuthorizationPoolProgressFixture.AuthorizationCapacity + AuthorizationPoolProgressFixture.EvidenceCapacity)
            .And.OnlyHaveUniqueItems();
        await using var observer = new NpgsqlConnection(fixture.DirectConnectionString);
        await observer.OpenAsync(deadline.Token);
        await using var active = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity WHERE pid = ANY(@pids)
              AND xact_start IS NOT NULL AND state = 'idle in transaction'
            """, observer);
        active.Parameters.AddWithValue("pids", pids.ToArray());
        (await active.ExecuteScalarAsync(deadline.Token)).Should().Be((long)pids.Count);
        output.WriteLine("Observed 2+4+2 distinct reserved application backends; owned PostgreSQL publication remapped: "
            + fixture.DatabasePublicationWasRemapped);
        // The evidence alias has a real backend reservation. Its production evidence owner ships in Step 4.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Admitted_real_authorization_completes_while_every_business_server_transaction_is_held(
        bool transactionPooled, bool postLock)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var trusted = new CandidateSource();
        var candidate = trusted.Candidate();
        var authorizationName = "authorization-progress-" + Guid.NewGuid().ToString("N");
        await using var authorization = new IdentityAuthorizationReadSource(
            fixture.ConnectionString(transactionPooled, "authorization", authorizationName));
        await authorization.QualifyAsync(IdentityMembershipFixture.Authority.AuthorityId,
            IdentityMembershipFixture.Authority.Issuer, deadline.Token);
        await using var parents = await HeldBusinessOwners.OpenAsync(
            fixture.ConnectionString(transactionPooled, "business", "business-progress"), deadline.Token);
        if (postLock) await parents.HoldInvariantLocksAsync(deadline.Token);
        var observer = new LifecycleObserver(fixture.DirectConnectionString, parents);
        var reader = new PreContextIdentityAuthorizationReader(authorization, observer);

        IdentityAuthorizationResult result;
        if (postLock)
        {
            // The same business owners still hold their actual User/Tenant invariant locks.
            result = await reader.ReadAsync(candidate, deadline.Token);
        }
        else
        {
            await using var nested = await parents.Units[0].BeginTransactionAsync(deadline.Token);
            nested.IsOwner.Should().BeFalse();
            result = await reader.ReadAsync(candidate, deadline.Token);
            await nested.CompleteAsync(deadline.Token);
        }

        // The affirmative facts come from the production Identity statement and real Tenancy reader.
        result.Outcome.Should().Be(IdentityAuthorizationOutcome.Covered);
        result.Memberships.Should().ContainSingle().Which.MembershipId.Should().Be(IdentityMembershipFixture.AdminMembershipA);
        result.Memberships.SelectMany(membership => membership.Roles).SelectMany(role => role.Grants)
            .Should().Contain(grant => grant.Scope == PermissionScope.Tenant && grant.Key == "identity.membership.admin");
        observer.Calls.Should().Be(1);
        observer.ChildWasDistinct.Should().BeTrue();
        observer.ChildMode.Should().Be(TransactionMode.ReadOnly);
        observer.AuthorizationBackendPids.Should().ContainSingle();
        observer.AuthorizationBackendPids.Should().NotIntersectWith(parents.BackendPids);
        observer.AuthorizationTransactionsDuringChild.Should().Be(1);
        observer.BusinessTransactionsDuringChild.Should().Be(AuthorizationPoolProgressFixture.BusinessCapacity);
        await AssertParentsStillHeldAsync(parents, postLock, deadline.Token);
        // Disposal below is the first parent release; completion above cannot borrow a released server.
    }

    [Fact]
    public async Task A_separate_client_pool_on_the_saturated_business_alias_fails_the_same_progress_control()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var trusted = new CandidateSource();
        var candidate = trusted.Candidate();
        var name = "dirty-authorization-progress-" + Guid.NewGuid().ToString("N");
        // Deliberately route the separate authorization client pool into the business server pool.
        await using var authorization = new IdentityAuthorizationReadSource(
            fixture.ConnectionString(true, "business", name));
        await authorization.QualifyAsync(IdentityMembershipFixture.Authority.AuthorityId,
            IdentityMembershipFixture.Authority.Issuer, deadline.Token);
        var parents = await HeldBusinessOwners.OpenAsync(
            fixture.ConnectionString(true, "business", "dirty-business-progress"), deadline.Token);
        try
        {
            await parents.HoldInvariantLocksAsync(deadline.Token);
            var observer = new LifecycleObserver(fixture.DirectConnectionString, parents);
            var reader = new PreContextIdentityAuthorizationReader(authorization, observer);
            using (var refused = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
            {
                await FluentActions.Awaiting(() => reader.ReadAsync(candidate, refused.Token))
                    .Should().ThrowAsync<OperationCanceledException>();
            }
            observer.Calls.Should().Be(0, "the saturated downstream pool cannot start the real authorization statements");
            await AssertParentsStillHeldAsync(parents, postLock: true, deadline.Token);
        }
        finally { await parents.DisposeAsync(); }

        // The dirty source itself works once the occupied downstream business servers return.
        await using var ambientSource = ApplicationDataSource.Build(
            fixture.ConnectionString(true, "business", "dirty-recovery-ambient"));
        await using var ambient = new NpgsqlUnitOfWork(
            ambientSource, NullLogger<NpgsqlUnitOfWork>.Instance);
        var recovered = new PreContextIdentityAuthorizationReader(authorization, new IdentityScopeLifecycleReader(ambient));
        (await recovered.ReadAsync(candidate, deadline.Token)).Outcome.Should().Be(IdentityAuthorizationOutcome.Covered);
    }

    private async Task AssertParentsStillHeldAsync(HeldBusinessOwners parents, bool postLock, CancellationToken cancellationToken)
    {
        parents.Units.Should().OnlyContain(unit => unit.HasActiveTransaction);
        parents.BackendPids.Should().OnlyHaveUniqueItems().And.HaveCount(AuthorizationPoolProgressFixture.BusinessCapacity);
        await using var observer = new NpgsqlConnection(fixture.DirectConnectionString);
        await observer.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_stat_activity
            WHERE pid = ANY(@pids) AND usename = current_user
              AND xact_start IS NOT NULL AND state = 'idle in transaction'
            """, observer);
        command.Parameters.AddWithValue("pids", parents.BackendPids.ToArray());
        (await command.ExecuteScalarAsync(cancellationToken)).Should().Be((long)AuthorizationPoolProgressFixture.BusinessCapacity);
        if (!postLock) return;
        command.CommandText = "SELECT count(*) FROM pg_locks WHERE pid = ANY(@pids) AND locktype = 'advisory' AND granted";
        (await command.ExecuteScalarAsync(cancellationToken)).Should().Be(2L * AuthorizationPoolProgressFixture.BusinessCapacity);
    }

    private sealed class CandidateSource : AuthorizationCandidate.MintingSource
    {
        public AuthorizationCandidate Candidate() => ForCandidate(AuthorizationSourceKind.Http,
            UserId.From(IdentityMembershipFixture.AdminA), TenantId.From(SchemaFixture.TenantA),
            OrganizationId.From(SchemaFixture.OrgA1));
    }

    /// <summary>Probes the actual child PID in this fixture, observes held transactions separately, then runs real lifecycle SQL.</summary>
    private sealed class LifecycleObserver(string directConnection, HeldBusinessOwners parents)
        : IIdentityScopeLifecycleReader
    {
        private readonly IdentityScopeLifecycleReader _actual = new(parents.Units[0]);
        public int Calls { get; private set; }
        public bool ChildWasDistinct { get; private set; }
        public TransactionMode? ChildMode { get; private set; }
        public List<int> AuthorizationBackendPids { get; } = [];
        public long AuthorizationTransactionsDuringChild { get; private set; }
        public long BusinessTransactionsDuringChild { get; private set; }

        public Task<IdentityScopeLifecycle> ReadAsync(TenantId tenantId, OrganizationId? organizationId,
            CancellationToken cancellationToken = default) => _actual.ReadAsync(tenantId, organizationId, cancellationToken);

        public async Task<IdentityScopeLifecycle> ReadAuthorizationAsync(IUnitOfWork child, AuthorizationCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            ChildWasDistinct = parents.Units.All(parent => !ReferenceEquals(parent, child));
            ChildMode = child.Mode;
            // Fixture-only instrumentation identifies the backend held by the actual production child.
            // PgBouncer application-name tracking supplies no identity or capacity evidence here.
            await using (var pid = new NpgsqlCommand("SELECT pg_backend_pid()", (NpgsqlConnection)child.Connection,
                (NpgsqlTransaction)child.Transaction!))
                AuthorizationBackendPids.Add((int)(await pid.ExecuteScalarAsync(cancellationToken))!);
            await using var connection = new NpgsqlConnection(directConnection);
            await connection.OpenAsync(cancellationToken);
            await using (var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity
                WHERE pid = @child AND usename = current_user
                  AND datname = current_database() AND backend_type = 'client backend'
                  AND xact_start IS NOT NULL
                """, connection))
            {
                command.Parameters.AddWithValue("child", AuthorizationBackendPids.Single());
                AuthorizationTransactionsDuringChild = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            }
            await using (var command = new NpgsqlCommand("""
                SELECT count(*) FROM pg_stat_activity WHERE pid = ANY(@pids)
                  AND xact_start IS NOT NULL AND state = 'idle in transaction'
                """, connection))
            {
                command.Parameters.AddWithValue("pids", parents.BackendPids.ToArray());
                BusinessTransactionsDuringChild = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
            }
            return await _actual.ReadAuthorizationAsync(child, candidate, cancellationToken);
        }
    }

    private sealed class HeldBusinessOwners : IAsyncDisposable
    {
        private readonly List<IUnitOfWorkScope> _frames = [];
        public NpgsqlDataSource Source { get; }
        public List<NpgsqlUnitOfWork> Units { get; } = [];
        public List<int> BackendPids { get; } = [];
        private HeldBusinessOwners(string connectionString) => Source = ApplicationDataSource.Build(connectionString);

        public static async Task<HeldBusinessOwners> OpenAsync(string connectionString, CancellationToken cancellationToken)
        {
            var owners = new HeldBusinessOwners(connectionString);
            try
            {
                for (var index = 0; index < AuthorizationPoolProgressFixture.BusinessCapacity; index++)
                {
                    var unit = new NpgsqlUnitOfWork(owners.Source, NullLogger<NpgsqlUnitOfWork>.Instance);
                    owners.Units.Add(unit);
                    owners._frames.Add(await unit.BeginTransactionAsync(cancellationToken));
                    await unit.SetTenantContextAsync(new ParentContext(), cancellationToken);
                    await using var pid = new NpgsqlCommand("SELECT pg_backend_pid()", (NpgsqlConnection)unit.Connection,
                        (NpgsqlTransaction)unit.Transaction!);
                    owners.BackendPids.Add((int)(await pid.ExecuteScalarAsync(cancellationToken))!);
                }
                return owners;
            }
            catch
            {
                await owners.DisposeAsync();
                throw;
            }
        }

        public async Task HoldInvariantLocksAsync(CancellationToken cancellationToken)
        {
            for (var index = 0; index < Units.Count; index++)
            {
                var unit = Units[index];
                await using var command = new NpgsqlCommand("""
                    SELECT pg_advisory_xact_lock(@user_key);
                    SELECT pg_advisory_xact_lock_shared(@tenant_key);
                    """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
                command.Parameters.AddWithValue("user_key", IdentityAdvisoryLockKey.ForUser(UserId.From(
                    index == 0 ? IdentityMembershipFixture.AdminA : IdentityMembershipFixture.PersonA)));
                command.Parameters.AddWithValue("tenant_key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(SchemaFixture.TenantA)));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            var failed = false;
            foreach (var frame in _frames.AsEnumerable().Reverse())
                try { await frame.DisposeAsync(); } catch (Exception) { failed = true; }
            foreach (var unit in Units.AsEnumerable().Reverse())
                try { await unit.DisposeAsync(); } catch (Exception) { failed = true; }
            try { await Source.DisposeAsync(); } catch (Exception) { failed = true; }
            if (failed) throw new InvalidOperationException("Held business owner cleanup failed.");
        }
    }

    private sealed class RawBackendReservations : IAsyncDisposable
    {
        private readonly List<NpgsqlConnection> _connections = [];
        private readonly List<NpgsqlTransaction> _transactions = [];

        public async Task<(NpgsqlConnection Connection, NpgsqlTransaction Transaction)> OpenAsync(
            string connectionString, CancellationToken cancellationToken)
        {
            var parsed = new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false };
            var connection = new NpgsqlConnection(parsed.ConnectionString);
            _connections.Add(connection);
            await connection.OpenAsync(cancellationToken);
            var transaction = await connection.BeginTransactionAsync(cancellationToken);
            _transactions.Add(transaction);
            return (connection, transaction);
        }

        public async ValueTask DisposeAsync()
        {
            var failed = false;
            foreach (var transaction in _transactions.AsEnumerable().Reverse())
                try { await transaction.DisposeAsync(); } catch (Exception) { failed = true; }
            foreach (var connection in _connections.AsEnumerable().Reverse())
                try { await connection.DisposeAsync(); } catch (Exception) { failed = true; }
            if (failed) throw new InvalidOperationException("Raw backend reservation cleanup failed.");
        }
    }

    private sealed class ParentContext : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => TenantId.From(SchemaFixture.TenantA);
        public OrganizationId? OrganizationId => LearnStack.SharedKernel.Identifiers.OrganizationId.From(SchemaFixture.OrgA1);
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.From(IdentityMembershipFixture.AdminA);
        public TenantContextOrigin? Origin => TenantContextOrigin.HostAndClaim;
        public string? CorrelationId => null;
        public string? ModuleName => "identity";
    }
}
