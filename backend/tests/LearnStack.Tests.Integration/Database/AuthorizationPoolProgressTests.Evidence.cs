using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Infrastructure.Authorization;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AuthorizationPoolProgressTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Actual_denial_is_durable_before_readonly_or_ownerless_success_while_all_business_backends_are_held(
        bool transactionPooled, bool readOnlyOwner)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var trusted = new CandidateSource(IdentityMembershipFixture.PersonA);
        await using var authorization = new IdentityAuthorizationReadSource(
            fixture.ConnectionString(transactionPooled, "authorization", "evidence-policy-progress"));
        await authorization.QualifyAsync(IdentityMembershipFixture.Authority.AuthorityId, IdentityMembershipFixture.Authority.Issuer, deadline.Token);
        await using var evidence = new AuthorizationEvidenceSource(
            fixture.ConnectionString(transactionPooled, "evidence", "evidence-store-progress"));
        await evidence.QualifyAsync(IdentityMembershipFixture.Authority.AuthorityId, IdentityMembershipFixture.Authority.Issuer, deadline.Token);
        await using var parents = await HeldBusinessOwners.OpenAsync(
            fixture.ConnectionString(transactionPooled, "business", "evidence-held-business"), deadline.Token,
            readOnlyOwner ? TransactionMode.ReadOnly : TransactionMode.ReadWrite, orgless: !readOnlyOwner);
        if (!readOnlyOwner) await PlantUncommittedDirectoryGrantAsync(parents.Units[0], deadline.Token);
        await using var unusedSource = ApplicationDataSource.Build(
            fixture.ConnectionString(transactionPooled, "business", "evidence-ownerless-unused"));
        await using var ownerless = new NpgsqlUnitOfWork(unusedSource, NullLogger<NpgsqlUnitOfWork>.Instance);
        var observer = new LifecycleObserver(fixture.DirectConnectionString, parents);
        var reader = new CommittedIdentityAuthorizationReader(new PreContextIdentityAuthorizationSessionFactory(authorization), observer);
        var evidenceObserver = new EvidenceObserver(evidence, parents, fixture.DirectConnectionString);
        using var execution = DirectoryExecution(reader, trusted, readOnlyOwner ? parents.Units[0] : ownerless, evidenceObserver);
        var publishedSuccess = false;

        await using (execution.Evidence.BeginExecution())
        {
            var denied = await execution.Operations.CheckHttpAsync(typeof(ListMembershipsQuery), new object(), deadline.Token);
            denied.IsFailure.Should().BeTrue();
            denied.Error!.Code.Should().Be("forbidden", "the real Learner has membership coverage and lacks the directory capability");
            evidenceObserver.Occurrences.Should().BeEmpty("an inner refusal remains pending while the outer execution can absorb it");
            publishedSuccess.Should().BeFalse();
            if (readOnlyOwner)
            {
                (await execution.Evidence.BeforeOwnerCompletionAsync(parents.Units[0], deadline.Token)).Should().BeNull();
            }
            else await execution.Evidence.BeforeSuccessAsync(deadline.Token);

            var occurrence = evidenceObserver.Occurrences.Should().ContainSingle().Which;
            await AssertDenialDurableAsync(occurrence, deadline.Token);
            await AssertParentsStillHeldAsync(parents, postLock: false, deadline.Token);
            evidenceObserver.ChildPids.Should().ContainSingle().And.NotIntersectWith(parents.BackendPids);
            evidenceObserver.HeldBusinessTransactions.Should().Be(AuthorizationPoolProgressFixture.BusinessCapacity);
            evidenceObserver.ActualChildMode.Should().Be(TransactionMode.ReadWrite);
            evidenceObserver.ActualChildRole.Should().Be("learnstack_app");
            observer.ChildWasDistinct.Should().BeTrue();
            observer.AuthorizationBackendPids.Should().NotIntersectWith(parents.BackendPids).And.NotIntersectWith(evidenceObserver.ChildPids);
            if (readOnlyOwner) await parents.CompleteFirstAsync(deadline.Token);
            else ownerless.HasActiveTransaction.Should().BeFalse("the otherwise-successful ownerless flow acquired no business server");
            publishedSuccess = true;
        }
        publishedSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Evidence_on_the_saturated_business_server_alias_blocks_success_and_the_same_source_recovers_after_release()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var trusted = new CandidateSource(IdentityMembershipFixture.PersonA);
        await using var authorization = new IdentityAuthorizationReadSource(fixture.ConnectionString(true, "authorization", "dirty-evidence-authorization"));
        await authorization.QualifyAsync(IdentityMembershipFixture.Authority.AuthorityId, IdentityMembershipFixture.Authority.Issuer, deadline.Token);
        // A separate Npgsql pool is insufficient when PgBouncer routes it into the same occupied server pool.
        await using var evidence = new AuthorizationEvidenceSource(fixture.ConnectionString(true, "business", "dirty-evidence-source"));
        await evidence.QualifyAsync(IdentityMembershipFixture.Authority.AuthorityId, IdentityMembershipFixture.Authority.Issuer, deadline.Token);
        await using var unusedSource = ApplicationDataSource.Build(fixture.ConnectionString(true, "business", "dirty-evidence-unused"));
        await using var ownerless = new NpgsqlUnitOfWork(unusedSource, NullLogger<NpgsqlUnitOfWork>.Instance);
        var parents = await HeldBusinessOwners.OpenAsync(fixture.ConnectionString(true, "business", "dirty-evidence-held"), deadline.Token);
        try
        {
            var reader = new CommittedIdentityAuthorizationReader(new PreContextIdentityAuthorizationSessionFactory(authorization),
                new LifecycleObserver(fixture.DirectConnectionString, parents));
            var observer = new EvidenceObserver(evidence, parents, fixture.DirectConnectionString);
            using var execution = DirectoryExecution(reader, trusted, ownerless, observer);
            await using (execution.Evidence.BeginExecution())
            {
                (await execution.Operations.CheckHttpAsync(typeof(ListMembershipsQuery), new object(), deadline.Token)).Error!.Code.Should().Be("forbidden");
                using var blocked = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await FluentActions.Awaiting(() => execution.Evidence.BeforeSuccessAsync(blocked.Token)).Should().ThrowAsync<OperationCanceledException>();
                execution.Health.IsHealthy.Should().BeFalse();
                observer.ChildPids.Should().BeEmpty("the dirty lane cannot obtain a downstream backend while both business servers stay held");
                await AssertParentsStillHeldAsync(parents, postLock: false, deadline.Token);
            }
        }
        finally { await parents.DisposeAsync(); }

        var recoveredReader = new CommittedIdentityAuthorizationReader(new PreContextIdentityAuthorizationSessionFactory(authorization),
            new LearnStack.Modules.Tenancy.Infrastructure.Persistence.IdentityScopeLifecycleReader(ownerless));
        var recoveredObserver = new EvidenceObserver(evidence, null, fixture.DirectConnectionString);
        using var recovered = DirectoryExecution(recoveredReader, trusted, ownerless, recoveredObserver);
        await using (recovered.Evidence.BeginExecution())
        {
            (await recovered.Operations.CheckHttpAsync(typeof(ListMembershipsQuery), new object(), deadline.Token)).Error!.Code.Should().Be("forbidden");
            await recovered.Evidence.BeforeSuccessAsync(deadline.Token);
            await AssertDenialDurableAsync(recoveredObserver.Occurrences.Should().ContainSingle().Which, deadline.Token);
            recovered.Health.IsHealthy.Should().BeTrue();
        }
    }

    private async Task AssertDenialDurableAsync(AuthorizationEvidenceOccurrence occurrence, CancellationToken cancellationToken)
    {
        var draft = occurrence.Draft;
        await using var connection = new NpgsqlConnection(fixture.DirectConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await IdentityMembershipFixture.ScopeAsync(connection, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM public.audit_log
            WHERE id = @id AND timestamp = @timestamp AND tenant_id = @tenant AND organization_id = @organization
              AND actor_user_id = @actor AND operation = 'identity.authorization.deny'
              AND operation_class = 'Must' AND outcome = 'denied'
              AND metadata::jsonb IS NOT DISTINCT FROM @metadata::jsonb
            """, connection, transaction);
        command.Parameters.AddWithValue("id", draft.Id.Value);
        command.Parameters.AddWithValue("timestamp", draft.Timestamp);
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        command.Parameters.AddWithValue("organization", SchemaFixture.OrgA1);
        command.Parameters.AddWithValue("actor", IdentityMembershipFixture.PersonA);
        command.Parameters.AddWithValue("metadata", draft.Metadata!);
        (await command.ExecuteScalarAsync(cancellationToken)).Should().Be(1L, "another real application transaction already observes the fixed denial before success");
    }

    private static async Task PlantUncommittedDirectoryGrantAsync(NpgsqlUnitOfWork parent, CancellationToken cancellationToken)
    {
        // If authorization accidentally borrowed this parent, the Learner would
        // appear to have the directory capability. The real child must ignore it.
        await using var command = new NpgsqlCommand("""
            SELECT pg_advisory_xact_lock_shared(@tenant_key);
            INSERT INTO public.role_permission_grants (tenant_id, role_id, permission_scope, permission_key)
                VALUES (@tenant, @role, 'Tenant', 'identity.membership.admin');
            """, (NpgsqlConnection)parent.Connection, (NpgsqlTransaction)parent.Transaction!);
        command.Parameters.AddWithValue("tenant_key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(SchemaFixture.TenantA)));
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        command.Parameters.AddWithValue("role", IdentityMembershipFixture.RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
        await command.ExecuteNonQueryAsync(cancellationToken);
        command.CommandText = """
            SELECT count(*) FROM public.role_permission_grants
                WHERE tenant_id = @tenant AND role_id = @role AND permission_key = 'identity.membership.admin'
            """;
        (await command.ExecuteScalarAsync(cancellationToken)).Should().Be(1L);
    }

    private sealed class EvidenceObserver(AuthorizationEvidenceSource actual, HeldBusinessOwners? parents, string directConnection)
        : IAuthorizationEvidenceSessionFactory
    {
        public List<AuthorizationEvidenceOccurrence> Occurrences { get; } = [];
        public List<int> ChildPids { get; } = [];
        public long HeldBusinessTransactions { get; private set; }
        public TransactionMode? ActualChildMode { get; private set; }
        public string? ActualChildRole { get; private set; }

        public async Task<IAuthorizationEvidenceSession?> OpenAsync(AuthorizationEvidenceOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            var session = await actual.OpenAsync(occurrence, cancellationToken);
            if (session is null) return null;
            try
            {
                Occurrences.Add(occurrence);
                ActualChildMode = session.Unit.Mode;
                await using (var pid = new NpgsqlCommand("SELECT pg_backend_pid(), current_user", (NpgsqlConnection)session.Unit.Connection,
                    (NpgsqlTransaction)session.Unit.Transaction!))
                await using (var row = await pid.ExecuteReaderAsync(session.DeadlineToken))
                {
                    (await row.ReadAsync(session.DeadlineToken)).Should().BeTrue();
                    ChildPids.Add(row.GetInt32(0));
                    ActualChildRole = row.GetString(1);
                }
                if (parents is not null)
                {
                    await using var observer = new NpgsqlConnection(directConnection);
                    await observer.OpenAsync(cancellationToken);
                    await using var held = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE pid = ANY(@pids) AND xact_start IS NOT NULL", observer);
                    held.Parameters.AddWithValue("pids", parents.BackendPids.ToArray());
                    HeldBusinessTransactions = (long)(await held.ExecuteScalarAsync(cancellationToken))!;
                }
                return session; // The actual production session still owns its connector, transaction and cleanup.
            }
            catch { await session.DisposeAsync(); throw; }
        }
    }

    private sealed class PolicyExecution : IDisposable
    {
        private readonly ServiceProvider _metrics = new ServiceCollection().AddMetrics().BuildServiceProvider();
        public AuditHealth Health { get; } = new();
        public AuthorizationEvidenceCoordinator Evidence { get; }
        public OperationAuthorizationCoordinator Operations { get; }

        public PolicyExecution(IOperationPolicyCatalog catalog, IIdentityAuthorizationReader reader, ITenantContext context,
            CandidateSource trusted, IUnitOfWork unit, IAuthorizationEvidenceSessionFactory? evidence)
        {
            var capture = new AuditStateCapture();
            var store = new PostgresAuditStore(capture,
                new Lazy<NpgsqlDataSource>(() => throw new InvalidOperationException("The evidence proof must not borrow the legacy business audit datasource.")),
                NullLogger<PostgresAuditStore>.Instance, _metrics.GetRequiredService<IMeterFactory>(), Health,
                new AuditCatalog([new IdentityAuditCatalogSource()]), new StaticTenantContextAccessor(context), new GlobalIdentityEnrollment(), evidence);
            OperationAuthorizationCoordinator? current = null;
            Evidence = new AuthorizationEvidenceCoordinator(store, capture, unit, new FixedClock(DateTimeOffset.UtcNow), new SystemGuidFactory(),
                () => current ?? throw new InvalidOperationException("The real policy owner is not attached."), [], NullLogger<AuthorizationEvidenceCoordinator>.Instance);
            Operations = current = new OperationAuthorizationCoordinator(catalog, new IdentityOperationAuthorizer(reader, catalog), context, [trusted], [], Evidence);
        }

        public void Dispose() { Operations.Dispose(); Evidence.Dispose(); _metrics.Dispose(); }
    }
}
