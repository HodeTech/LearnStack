using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Text.Json;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Audit.Capture;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace LearnStack.Tests.AuthorizationEvidenceProof;

public enum ResourcePopulation { Off, Should, May, OverrideOff, Plural }

/// <summary>
/// Mechanism proof only: fixture policies supply bounded decisions, while the actual
/// common audit/operation/transaction owners, module entity, RLS, store and evidence
/// source enforce durability. These fixtures claim no production permission coverage.
/// Linked into a separate killable process so a finally cannot rescue its rows.
/// </summary>
internal sealed class AuthorizationBarrierHarness : IAsyncDisposable
{
    private readonly NpgsqlDataSource _business;
    private readonly AuthorizationEvidenceSource _source;
    private readonly NpgsqlUnitOfWork _physical;
    private readonly CandidateSource _candidates;
    private readonly Meters _meters = new();
    private readonly DomainEventCollector _collector;
    private readonly ServiceProvider _contexts;
    private readonly AuthorizationEvidenceCoordinator _evidence;
    private readonly OperationAuthorizationCoordinator _authorization;
    private readonly AuditExecutionCoordinator _audit;
    private readonly TransactionExecutionCoordinator _transactions;
    private readonly TenantContext _context;
    private readonly IClock _clock = new SystemClock();

    private AuthorizationBarrierHarness(NpgsqlDataSource business, AuthorizationEvidenceSource source,
        TenantContext context, ResourcePopulation population, bool compositionFault,
        Func<bool, Task>? commitBoundary, TenantSettingId? businessId)
    {
        _business = business;
        _source = source;
        _context = context;
        BusinessId = businessId ?? TenantSettingId.From(Guid.CreateVersion7());
        _candidates = new CandidateSource(context);
        _physical = new NpgsqlUnitOfWork(business, NullLogger<NpgsqlUnitOfWork>.Instance);
        Unit = commitBoundary is null ? _physical : new BoundaryUnit(_physical, commitBoundary);
        Factory = new ControlledFactory(source);
        Health = new AuditHealth();
        Capture = new AuditStateCapture();
        var catalog = new AuditCatalog([new Catalog(population)]);
        var store = new PostgresAuditStore(Capture, new Lazy<NpgsqlDataSource>(() => business),
            StoreLogs, _meters, Health, catalog, new StaticTenantContextAccessor(context), new GlobalIdentityEnrollment(), Factory);
        Store = new ObservedStore(store, Capture);
        OperationAuthorizationCoordinator? authorization = null;
        _evidence = new AuthorizationEvidenceCoordinator(Store, Capture, Unit, _clock, new SystemGuidFactory(),
            () => authorization ?? throw new InvalidOperationException("The proof invocation owner is unavailable."),
            compositionFault ? [new FaultingTransport()] : [], CompositionLogs);
        _authorization = authorization = new OperationAuthorizationCoordinator(new OperationPolicyCatalog([new PolicySource()]),
            new Decisions(this), context, [_candidates], [], _evidence);
        var producer = new ProducerExecution(Unit);
        _collector = new DomainEventCollector(producer, new EmptyPublisher());
        _transactions = new TransactionExecutionCoordinator(Unit, context, Store, Capture, producer, _collector,
            NullLogger<TransactionExecutionCoordinator>.Instance, authorization, _evidence);
        var cache = new InMemoryCacheService(_clock, _meters);
        if (population == ResourcePopulation.OverrideOff)
            cache.SetAsync<IReadOnlyDictionary<string, bool>>(AuditConfigService.CacheKeyFor(context.TenantId),
                new Dictionary<string, bool>(StringComparer.Ordinal) { ["proof|proof.inner.read"] = false }).GetAwaiter().GetResult();
        var classifier = new AuditConfigService(cache, new Lazy<NpgsqlDataSource>(() => business),
            NullLogger<AuditConfigService>.Instance, new StaticTenantContextAccessor(context));
        _audit = new AuditExecutionCoordinator(catalog, classifier, Capture, Store, context, _clock,
            new SystemGuidFactory(), NullLogger<AuditExecutionCoordinator>.Instance, Unit, _evidence);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Unit);
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(context));
        services.AddSingleton<ISaveChangesInterceptor>(new AuditChangeTrackerInterceptor(Capture, Unit));
        services.AddModuleDbContext<TenancyDbContext>();
        _contexts = services.BuildServiceProvider();
    }

    internal IUnitOfWork Unit { get; }
    internal AuditStateCapture Capture { get; }
    internal AuditHealth Health { get; }
    internal ControlledFactory Factory { get; }
    internal ObservedStore Store { get; }
    internal ProofLogs<PostgresAuditStore> StoreLogs { get; } = new();
    internal ProofLogs<AuthorizationEvidenceCoordinator> CompositionLogs { get; } = new();
    internal ProtectedOperationOutcome InnerOutcome { get; set; } = ProtectedOperationOutcome.Forbidden;
    internal TenantSettingId BusinessId { get; }
    internal Result<None>? InnerResult { get; private set; }
    internal bool DeniedHandlerEntered { get; private set; }

    internal static async Task<AuthorizationBarrierHarness> CreateAsync(string connectionString, IdentityAuthorityId authority,
        string issuer, TenantId tenant, OrganizationId organization, UserId actor, string correlation,
        ResourcePopulation population = ResourcePopulation.Off, bool compositionFault = false,
        Func<bool, Task>? commitBoundary = null, TenantSettingId? businessId = null)
    {
        var context = TenantContextFactory.Create(new TenantResolutionAttempt
        {
            HasValidatedPrincipal = true,
            UserId = actor,
            HostTenantId = tenant,
            HostOrganizationId = organization,
            ClaimTenantId = tenant,
            ClaimOrganizationId = organization,
            MembershipCovers = true,
            ClaimedOrganizationBelongsToTenant = true,
            CorrelationId = correlation,
        }).Value ?? throw new InvalidOperationException("The fixture scope was refused.");
        var business = ApplicationDataSource.Build(connectionString);
        var source = new AuthorizationEvidenceSource(connectionString);
        try
        {
            await source.QualifyAsync(authority, issuer);
            return new(business, source, context, population, compositionFault, commitBoundary, businessId);
        }
        catch { await source.DisposeAsync(); await business.DisposeAsync(); throw; }
    }

    internal Task<Result<None>> RunAsync(TransactionMode? mode = TransactionMode.ReadWrite) =>
        _audit.ExecuteAsync(typeof(Outer), null, () => _authorization.ExecuteRequestAsync<Outer, Result<None>>(new Outer(),
            () => mode is { } transactionMode
                ? _transactions.ExecuteAsync(null, transactionMode, "evidence-mechanism-proof", Handler, CancellationToken.None)
                : Handler()), CancellationToken.None);

    internal Task<Result<None>> RefuseAsync() => DenyAsync();

    private async Task<Result<None>> Handler()
    {
        if (Unit.Mode == TransactionMode.ReadWrite)
        {
            var db = _contexts.GetRequiredService<TenancyDbContext>();
            var setting = TenantSetting.Create(BusinessId, _context.TenantId, _context.OrganizationId,
                "evidence-proof." + BusinessId.Value.ToString("N"), JsonSerializer.Serialize(_context.CorrelationId),
                _clock, _context.UserId!.Value);
            db.TenantSettings.Add(setting);
            await db.SaveChangesAsync();
        }
        else if (Unit.Mode == TransactionMode.ReadOnly)
        {
            var db = _contexts.GetRequiredService<TenancyDbContext>();
            _ = await db.TenantSettings.CountAsync();
        }
        InnerResult = await DenyAsync();
        return Result.Ok(None.Value);
    }

    private Task<Result<None>> DenyAsync() => _audit.ExecuteAsync(typeof(Inner), null,
        () => _authorization.ExecuteRequestAsync<Inner, Result<None>>(new Inner(), () =>
        {
            DeniedHandlerEntered = true;
            return Task.FromResult(Result.Ok(None.Value));
        }), CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        await _contexts.DisposeAsync();
        _authorization.Dispose();
        _evidence.Dispose();
        _candidates.Dispose();
        _collector.Dispose();
        await _physical.DisposeAsync();
        await _source.DisposeAsync();
        await _business.DisposeAsync();
        _meters.Dispose();
    }

    private sealed class Outer;
    private sealed class Inner;
    private sealed class SecondResource;

    private sealed class PolicySource : IOperationPolicySource
    {
        private static readonly PermissionDefinition Capability = new(PermissionScope.Organization, "proof.evidence.write");
        public IEnumerable<PermissionRegistration> Definitions => [new(Capability, true)];
        public IEnumerable<OperationPolicy> Policies =>
        [OperationPolicy.ForProtected<Outer>("proof.evidence.outer", Capability), OperationPolicy.ForProtected<Inner>("proof.evidence.inner", Capability)];
    }

    private sealed class Catalog(ResourcePopulation population) : IAuditCatalogSource
    {
        public string ModuleName => "proof";
        public void Describe(IAuditCatalogBuilder builder)
        {
            builder.DeclareOffPath("identity.authorization.deny", OperationType.SecurityEvent, OperationClass.Must);
            if (population == ResourcePopulation.Plural)
                builder.MustAudit<Outer>("proof.setting.create", OperationType.Create, typeof(TenantSetting))
                    .MustAudit<Outer>("proof.second.read", OperationType.ReadSensitive, typeof(SecondResource));
            else builder.Off<Outer>();
            if (population is ResourcePopulation.Should or ResourcePopulation.OverrideOff)
                builder.ShouldAudit<Inner>("proof.inner.read", OperationType.ReadSensitive, typeof(SecondResource));
            else if (population == ResourcePopulation.May)
                builder.MayAudit<Inner>("proof.inner.read", OperationType.ReadSensitive, typeof(SecondResource));
            else builder.Off<Inner>();
        }
    }

    private sealed class CandidateSource(TenantContext context) : AuthorizationCandidate.MintingSource, IAuthorizationCandidateSource
    {
        public AuthorizationCandidate? ForValidatedScope(TenantId tenant, OrganizationId? organization) =>
            tenant == context.TenantId && organization == context.OrganizationId
                ? ForCandidate(AuthorizationSourceKind.Http, context.UserId!.Value, tenant, organization) : null;
    }

    private sealed class Decisions(AuthorizationBarrierHarness owner) : ProtectedOperationDecision.MintingSource, IProtectedOperationAuthorizer
    {
        private readonly Guid _membership = Guid.CreateVersion7();
        public Task<ProtectedOperationDecision> AuthorizeAsync(OperationInvocation invocation, OperationPolicy policy,
            AuthorizationCandidate candidate, CancellationToken cancellationToken = default) => Task.FromResult(ForDecision(invocation, candidate,
                policy.RequestType == typeof(Outer) ? ProtectedOperationOutcome.Allowed : owner.InnerOutcome, [_membership]));
    }

    private sealed class FaultingTransport : IAuthorizationTransportSource
    {
        public OperationTransportProvenance? Capture(OperationInvocation invocation) => invocation.Policy.RequestType == typeof(Inner)
            ? throw new InvalidOperationException("Deliberate fixture composer fault.") : null;
    }

    private sealed class EmptyPublisher : IPublisher
    {
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class Meters : IMeterFactory
    {
        private readonly List<Meter> _meters = [];
        public Meter Create(MeterOptions options) { var meter = new Meter(options); _meters.Add(meter); return meter; }
        public void Dispose() { foreach (var meter in _meters) meter.Dispose(); }
    }

    internal sealed class ControlledFactory(IAuthorizationEvidenceSessionFactory source) : IAuthorizationEvidenceSessionFactory
    {
        internal TaskCompletionSource CommitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CommitRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool HoldCommit { get; set; }
        internal bool FailAcknowledgement { get; set; }
        internal int Opens { get; private set; }
        public async Task<IAuthorizationEvidenceSession?> OpenAsync(AuthorizationEvidenceOccurrence occurrence, CancellationToken cancellationToken = default)
        {
            Opens++;
            var session = await source.OpenAsync(occurrence, cancellationToken);
            return session is null ? null : new Session(session, this);
        }
        private sealed class Session(IAuthorizationEvidenceSession inner, ControlledFactory owner) : IAuthorizationEvidenceSession
        {
            public IUnitOfWork Unit => inner.Unit;
            public CancellationToken DeadlineToken => inner.DeadlineToken;
            public Task BeginVerificationAsync() => inner.BeginVerificationAsync();
            public async Task CompleteAsync()
            {
                owner.CommitEntered.TrySetResult();
                if (owner.HoldCommit) await owner.CommitRelease.Task.WaitAsync(DeadlineToken);
                await inner.CompleteAsync();
                if (owner.FailAcknowledgement) throw new IOException("Deliberate evidence acknowledgement loss.");
            }
            public void MarkCleanupFailed() => inner.MarkCleanupFailed();
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    internal sealed class ObservedStore(IAuditStore inner, IAuditStateCapture capture) : IAuditStore
    {
        internal List<AuthorizationEvidenceOccurrence> Pending { get; } = [];
        internal List<AuthorizationEvidenceOccurrence> Standalone { get; } = [];
        internal List<AuditIntentState> ReconciliationStates { get; } = [];
        public Task WriteAuthorizationPendingAsync(IUnitOfWork unit, AuthorizationEvidenceOccurrence occurrence, CancellationToken token = default)
        { Pending.Add(occurrence); return inner.WriteAuthorizationPendingAsync(unit, occurrence, token); }
        public Task WriteAuthorizationStandaloneAsync(AuthorizationEvidenceOccurrence occurrence, CancellationToken token = default)
        { Standalone.Add(occurrence); ReconciliationStates.Add(capture.State); return inner.WriteAuthorizationStandaloneAsync(occurrence, token); }
        public Task WritePendingAsync(IUnitOfWork unit, CancellationToken token = default) => inner.WritePendingAsync(unit, token);
        public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken token = default) => inner.WriteStandaloneAsync(entry, token);
        public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken token = default) => inner.WriteBestEffortAsync(entry, token);
        public Task WritePlatformScopeAsync(AuditEntryDraft entry, DbConnection connection, DbTransaction transaction, CancellationToken token = default) => inner.WritePlatformScopeAsync(entry, connection, transaction, token);
        public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token = default) => inner.WriteEventTerminalAsync(entry, transition, connection, transaction, token);
        public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition, DbConnection connection, DbTransaction transaction, CancellationToken token = default) => inner.WriteJobTerminalAsync(entry, transition, connection, transaction, token);
        public Task WriteGlobalIdentityPendingAsync(IUnitOfWork unit, GlobalIdentityCreationReference creation, CancellationToken token = default) => inner.WriteGlobalIdentityPendingAsync(unit, creation, token);
        public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes, AuditIntentState state, DateTimeOffset timestamp, CancellationToken token = default) => inner.ReconcileGlobalIdentityAsync(intent, changes, state, timestamp, token);
    }

    internal sealed class ProofLogs<T> : ILogger<T>
    {
        internal List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class BoundaryUnit(IUnitOfWork inner, Func<bool, Task> boundary) : IUnitOfWork
    {
        public DbConnection Connection => inner.Connection;
        public DbTransaction? Transaction => inner.Transaction;
        public bool HasActiveTransaction => inner.HasActiveTransaction;
        public TransactionMode? Mode => inner.Mode;
        public bool IsRollbackOnly => inner.IsRollbackOnly;
        public bool IsTenantContextIssuedOn(DbTransaction? transaction) => inner.IsTenantContextIssuedOn(transaction);
        public TenantContextAnnouncement? GetTenantContextAnnouncement(DbTransaction? transaction) => inner.GetTenantContextAnnouncement(transaction);
        public async Task<IUnitOfWorkScope> BeginTransactionAsync(TransactionMode mode, CancellationToken token = default) => new Scope(await inner.BeginTransactionAsync(mode, token), boundary);
        public Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken token = default) => BeginTransactionAsync(TransactionMode.ReadWrite, token);
        public Task SetTenantContextAsync(ITenantContext context, CancellationToken token = default) => inner.SetTenantContextAsync(context, token);
        public Task SetProvisioningTenantContextAsync(TenantId tenant, CancellationToken token = default) => inner.SetProvisioningTenantContextAsync(tenant, token);
        public Task CommitAsync(CancellationToken token = default) => inner.CommitAsync(token);
        public Task RollbackAsync(CancellationToken token = default) => inner.RollbackAsync(token);
        public void MarkRollbackOnly() => inner.MarkRollbackOnly();
        public ValueTask DisposeAsync() => inner.DisposeAsync();
        private sealed class Scope(IUnitOfWorkScope inner, Func<bool, Task> boundary) : IUnitOfWorkScope
        {
            public bool IsOwner => inner.IsOwner;
            public async Task CompleteAsync(CancellationToken token = default)
            {
                if (IsOwner) await boundary(false);
                await inner.CompleteAsync(token);
                if (IsOwner) await boundary(true);
            }
            public Task FailAsync(CancellationToken token = default) => inner.FailAsync(token);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
