using System.Diagnostics.Metrics;
using LearnStack.Api.Common;
using LearnStack.Application.Authorization;
using LearnStack.Application.Persistence;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Infrastructure.Authorization;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Explicit staged proof composition; launch still keeps its slot-5 registration unchanged.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class MembershipDirectoryPipelineTests(IdentityAuthorizationReaderFixture fixture)
    : IClassFixture<IdentityAuthorizationReaderFixture>
{
    private const long CommitGate = 7_601_077_003;

    [Fact]
    public async Task Actual_directory_result_waits_for_its_MUST_row_to_commit_before_disclosure()
    {
        const string Correlation = "directory-proof-commit-barrier";
        await InstallTriggerAsync(Correlation, "commit_gate");
        try
        {
            await using var proof = await PipelineProof.CreateAsync(fixture, Correlation);
            await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(blocker);
            await using var lockTransaction = await blocker.BeginTransactionAsync();
            await ExecuteAsync(blocker, lockTransaction, "SELECT pg_advisory_xact_lock(@key)", ("key", CommitGate));
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
            await using var observation = await observer.BeginTransactionAsync();
            await ScopeAsync(observer, observation, SchemaFixture.TenantA, SchemaFixture.OrgA1);
            await using var monitor = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var running = proof.Sender.Send(new ListMembershipsQuery(), deadline.Token);
            try
            {
                await ObservePhysicalCommitWaitAsync(monitor, blocker.ProcessID, running, deadline.Token);
                Assert.False(running.IsCompleted);
                Assert.Equal(0, await CountRowsAsync(observer, observation, Correlation));
            }
            finally
            {
                await lockTransaction.RollbackAsync(CancellationToken.None);
            }
            var result = await running;
            Assert.True(result.IsSuccess);
            Assert.Equal(4, result.Value.Items.Count);
            Assert.Equal(1, await CountRowsAsync(observer, observation, Correlation));
            Assert.Equal("success:Must:ReadSensitive:Membership", await ScalarAsync<string>(observer, observation, """
                SELECT outcome||':'||operation_class||':'||operation_type||':'||entity_type
                FROM audit_log WHERE operation='identity.membership.list' AND correlation_id=@correlation
                """, ("correlation", Correlation)));
            Assert.False(proof.Unit.HasActiveTransaction);
            Assert.False(proof.Unit.IsTenantWideRead);
        }
        finally { await RemoveTriggerAsync(); }
    }

    [Fact]
    public async Task Actual_MUST_insert_failure_withholds_the_materialized_directory_result()
    {
        const string Correlation = "directory-proof-insert-refused";
        await InstallTriggerAsync(Correlation, "insert_failure");
        try
        {
            await using var proof = await PipelineProof.CreateAsync(fixture, Correlation);
            Result<Page<MembershipDirectoryItem>>? published = null;
            var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(async () =>
                published = await proof.Sender.Send(new ListMembershipsQuery()));
            Assert.Equal("audit_unavailable", failure.Error.Code);
            Assert.Null(published);
            Assert.False(proof.Unit.HasActiveTransaction);
            Assert.False(proof.Provider.GetRequiredService<IAuditHealth>().IsHealthy);
            Assert.Equal(0, await CountRowsAsync(Correlation));
        }
        finally { await RemoveTriggerAsync(); }
    }

    [Fact]
    public async Task Actual_deferred_COMMIT_failure_answers_audit_unavailable_and_withholds_the_directory_result()
    {
        const string Correlation = "directory-proof-commit-refused";
        await InstallTriggerAsync(Correlation, "commit_failure");
        try
        {
            await using var proof = await PipelineProof.CreateAsync(fixture, Correlation);
            Result<Page<MembershipDirectoryItem>>? published = null;
            var failure = await Assert.ThrowsAsync<AuditWriteFailedException>(async () =>
                published = await proof.Sender.Send(new ListMembershipsQuery()));
            Assert.Equal("P0399", Assert.IsType<PostgresException>(failure.InnerException).SqlState);
            Assert.Equal("audit_unavailable", failure.Error.Code);
            Assert.Equal(503, HttpStatusMap.For(failure));
            Assert.Null(published);
            Assert.False(proof.Unit.HasActiveTransaction);
            Assert.False(proof.Provider.GetRequiredService<IAuditHealth>().IsHealthy);
            Assert.Equal(0, await CountRowsAsync(Correlation));
        }
        finally { await RemoveTriggerAsync(); }
    }

    [Fact]
    public async Task Ordinary_owner_preserves_its_primary_COMMIT_failure_even_with_a_sensitive_read_intent()
    {
        const string Correlation = "ordinary-proof-commit-refused";
        await InstallTriggerAsync(Correlation, "commit_failure");
        try
        {
            await using var proof = await PipelineProof.CreateAsync(fixture, Correlation);
            var failure = await Assert.ThrowsAsync<PostgresException>(() => proof.RunOrdinaryAuditedOwnerAsync(Correlation));
            Assert.Equal("P0399", failure.SqlState);
            Assert.Equal(500, HttpStatusMap.For(failure));
            Assert.Equal(AuditIntentState.Indeterminate, proof.Capture.State);
            Assert.Same(failure, proof.Capture.IndeterminateCause);
            Assert.False(proof.Unit.HasActiveTransaction);
            Assert.Equal(0, await CountRowsAsync(Correlation));
        }
        finally { await RemoveTriggerAsync(); }
    }

    private async Task InstallTriggerAsync(string correlation, string mode)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.Postgres.MigrationConnectionString);
        var action = mode == "commit_gate" ? $"PERFORM pg_catalog.pg_advisory_xact_lock({CommitGate});"
            : "RAISE EXCEPTION 'directory proof audit refusal' USING ERRCODE='P0399';";
        await ExecuteAsync(owner, null, $$"""
            CREATE FUNCTION public.fn_directory_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN
                IF NEW.operation='identity.membership.list' AND NEW.correlation_id='{{correlation}}' THEN
                    {{action}}
                END IF;
                RETURN NEW;
            END $proof$;
            """);
        if (mode == "insert_failure")
            await ExecuteAsync(owner, null, """
                CREATE TRIGGER tg_directory_audit_proof BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.fn_directory_audit_proof()
                """);
        else
            await ExecuteAsync(owner, null, """
                CREATE CONSTRAINT TRIGGER tg_directory_audit_proof AFTER INSERT ON public.audit_log
                DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_directory_audit_proof()
                """);
    }

    private async Task RemoveTriggerAsync()
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Membership.Postgres.MigrationConnectionString);
        await ExecuteAsync(owner, null, """
            DROP TRIGGER IF EXISTS tg_directory_audit_proof ON public.audit_log;
            DROP FUNCTION IF EXISTS public.fn_directory_audit_proof();
            """);
    }

    private static async Task ObservePhysicalCommitWaitAsync(NpgsqlConnection observer, int blocker, Task running,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await using var command = new NpgsqlCommand("""
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                    WHERE @blocker=ANY(pg_blocking_pids(pid)) AND query='COMMIT' AND state='active')
                """, observer);
            command.Parameters.AddWithValue("blocker", blocker);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken))!) return;
            if (running.IsCompleted)
            {
                await running;
                Assert.Fail("The directory completed before the physical COMMIT barrier was observed.");
            }
            await Task.Delay(20, cancellationToken);
        }
    }

    private async Task<long> CountRowsAsync(string correlation)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        return await CountRowsAsync(app, transaction, correlation);
    }

    private static Task<long> CountRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string correlation) =>
        ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM audit_log WHERE operation='identity.membership.list' AND correlation_id=@correlation",
            ("correlation", correlation));

    private sealed class PipelineProof : IAsyncDisposable
    {
        private readonly IdentityAuthorizationReadSource _authorizationSource;
        private readonly NpgsqlDataSource _businessSource;
        private readonly CandidateSource _candidateSource;
        private readonly AsyncServiceScope _scope;

        private PipelineProof(IdentityAuthorizationReadSource source, NpgsqlDataSource business, CandidateSource candidate,
            ServiceProvider provider)
        {
            _authorizationSource = source; _businessSource = business; _candidateSource = candidate;
            Provider = provider; _scope = provider.CreateAsyncScope();
        }
        public ServiceProvider Provider { get; }
        public ISender Sender => _scope.ServiceProvider.GetRequiredService<ISender>();
        public NpgsqlUnitOfWork Unit => (NpgsqlUnitOfWork)_scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        public AuditStateCapture Capture => _scope.ServiceProvider.GetRequiredService<AuditStateCapture>();

        public Task<Result<None>> RunOrdinaryAuditedOwnerAsync(string correlation)
        {
            Capture.DeclareIntent(new AuditIntent(AuditEntryId.From(Guid.CreateVersion7()), TenantId.From(SchemaFixture.TenantA),
                OrganizationId.From(SchemaFixture.OrgA1), UserId.From(AdminA), correlation, "identity", "identity.membership.list",
                OperationType.ReadSensitive, OperationClass.Must, typeof(LearnStack.Modules.Identity.Domain.Membership),
                DateTimeOffset.UtcNow));
            return _scope.ServiceProvider.GetRequiredService<TransactionExecutionCoordinator>()
                .ExecuteAsync(null, TransactionMode.ReadWrite, "ordinary-proof", () => Task.FromResult(Result.Ok(None.Value)));
        }

        public static async Task<PipelineProof> CreateAsync(IdentityAuthorizationReaderFixture fixture, string correlation)
        {
            var source = new IdentityAuthorizationReadSource(fixture.AppConnectionString);
            await source.QualifyAsync(Authority.AuthorityId, Authority.Issuer);
            var business = ApplicationDataSource.Build(fixture.AppConnectionString);
            var context = TenantContextFactory.Create(new TenantResolutionAttempt
            {
                HasValidatedPrincipal = true,
                UserId = UserId.From(AdminA),
                HostTenantId = TenantId.From(SchemaFixture.TenantA),
                HostOrganizationId = OrganizationId.From(SchemaFixture.OrgA1),
                ClaimTenantId = TenantId.From(SchemaFixture.TenantA),
                ClaimOrganizationId = OrganizationId.From(SchemaFixture.OrgA1),
                MembershipCovers = true,
                ClaimedOrganizationBelongsToTenant = true,
                CorrelationId = correlation,
            }).Value;
            Assert.NotNull(context);
            var candidateSource = new CandidateSource(context);
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMetrics();
            services.AddSingleton<IClock, SystemClock>();
            services.AddSingleton<IGuidFactory, SystemGuidFactory>();
            services.AddSingleton(business);
            services.AddSingleton(new Lazy<NpgsqlDataSource>(() => business));
            services.AddSingleton(source);
            services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
            services.AddTransient<ITenantContext>(_ => context);
            services.AddSingleton<IAuthorizationCandidateSource>(candidateSource);
            services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
            services.AddScoped<GlobalIdentityEnrollment>();
            services.AddScoped<IGlobalIdentityEnrollment>(provider => provider.GetRequiredService<GlobalIdentityEnrollment>());
            services.AddScoped<IGlobalIdentityScopeAccessor, GlobalIdentityScopeAccessor>();
            services.AddModuleDbContext<IdentityDbContext>();
            services.AddIdentityAuthorizationReader();
            services.AddIdentityOperationPolicies();
            services.AddScoped<IIdentityScopeLifecycleReader, IdentityScopeLifecycleReader>();
            services.AddSingleton<IOperationPolicyCatalog>(provider => new OperationPolicyCatalog(provider.GetServices<IOperationPolicySource>()));
            services.AddLearnStackOperationAuthorization();
            services.AddScoped<AuditStateCapture>();
            services.AddScoped<IAuditStateCapture>(provider => provider.GetRequiredService<AuditStateCapture>());
            services.AddScoped<IAuditSubject>(provider => provider.GetRequiredService<AuditStateCapture>());
            services.AddSingleton<IAuditCatalog>(new AuditCatalog([new IdentityAuditCatalogSource()]));
            services.AddSingleton<IAuditHealth, AuditHealth>();
            services.AddSingleton<ICacheService>(provider => new InMemoryCacheService(provider.GetRequiredService<IClock>(),
                provider.GetRequiredService<IMeterFactory>()));
            services.AddScoped<IAuditConfigService, AuditConfigService>();
            services.AddScoped<IAuditStore, PostgresAuditStore>();
            services.AddLearnStackBackgroundJobContracts();
            services.AddLearnStackMediatRPipeline(typeof(IdentityOperationPolicySource).Assembly);
            var slot = services.Select((descriptor, index) => (descriptor, index)).Single(pair =>
                pair.descriptor.ServiceType == typeof(IPipelineBehavior<,>)
                && pair.descriptor.ImplementationType == typeof(AuthorizationBehavior<,>));
            services[slot.index] = ServiceDescriptor.Transient(typeof(IPipelineBehavior<,>), typeof(ScopedAuthorizationBehavior<,>));
            var expected = MediatRPipelineRegistration.CanonicalBehaviorOrder.Select(type =>
                type == typeof(AuthorizationBehavior<,>) ? typeof(ScopedAuthorizationBehavior<,>) : type);
            Assert.Equal(expected, services.Where(descriptor => descriptor.ServiceType == typeof(IPipelineBehavior<,>))
                .Select(descriptor => descriptor.ImplementationType));
            return new(source, business, candidateSource, services.BuildServiceProvider());
        }

        public async ValueTask DisposeAsync()
        {
            await _scope.DisposeAsync();
            await Provider.DisposeAsync();
            _candidateSource.Dispose();
            await _businessSource.DisposeAsync();
            await _authorizationSource.DisposeAsync();
        }
    }

    private sealed class CandidateSource(TenantContext context) : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
    {
        public AuthorizationCandidate? ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            tenant == context.TenantId && organization == context.OrganizationId
                ? ForCandidate(AuthorizationSourceKind.Http, context.UserId!.Value, tenant, organization) : null;
    }
}
