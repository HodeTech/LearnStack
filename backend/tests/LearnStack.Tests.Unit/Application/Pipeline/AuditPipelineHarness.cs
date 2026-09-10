using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Tests.Unit.Application.Pipeline;

/// <summary>
/// The doubles the audit pipeline cases share.
/// </summary>
/// <remarks>
/// The <b>capture</b> is the real <see cref="AuditStateCapture"/> rather than a double:
/// it is the object whose state transitions the whole durability model turns on, and a
/// stub of it would let a case pass against a buffer that reported whatever the case
/// wanted. The store, the catalogue and the classifier are doubles because each one's own
/// behaviour is measured elsewhere — the store against a real database.
/// </remarks>
internal static class AuditPipelineHarness
{
    public static readonly Guid Tenant = Guid.Parse("aaaaaaaa-0000-7000-8000-000000000001");
    public static readonly Guid Organization = Guid.Parse("aaaaaaaa-0000-7000-8000-000000000002");
    public static readonly Guid Actor = Guid.Parse("aaaaaaaa-0000-7000-8000-000000000003");

    public static AuditCatalogEntry Entry(
        string operation = "tenancy.tenant.create",
        OperationClass operationClass = OperationClass.Must,
        Type? entityType = null) =>
        new(
            operation[..operation.IndexOf('.')],
            operation,
            OperationType.Create,
            operationClass,
            entityType ?? typeof(object));
}

/// <summary>A catalogue that answers whatever a case set up.</summary>
internal sealed class FakeCatalog(params AuditCatalogEntry[] entries) : IAuditCatalog
{
    private readonly AuditRegistration? _registration =
        entries.Length == 0 ? null : new AuditRegistration(false, entries);

    /// <summary>Set to register the request as silent rather than audited.</summary>
    public bool Silent { get; init; }

    /// <summary>Set to leave the request unregistered — the rejection.</summary>
    public bool Unregistered { get; init; }

    public IReadOnlyCollection<AuditCatalogEntry> All => _registration?.Entries ?? [];

    public bool TryGet(Type requestType, out AuditRegistration registration)
    {
        if (Unregistered)
        {
            registration = null!;
            return false;
        }

        registration = Silent ? new AuditRegistration(true, []) : _registration!;

        return true;
    }

    /// <summary>The off-path slugs a case declared, keyed by slug.</summary>
    /// <remarks>
    /// Empty by default. Nothing in the pipeline reads this — off-path operations are the
    /// ones with no request type for the pipeline to see — so a case that needs one sets
    /// it, and the default keeps every other case honest about not using it.
    /// </remarks>
    public IReadOnlyDictionary<string, AuditCatalogEntry> OffPath { get; init; } =
        new Dictionary<string, AuditCatalogEntry>(StringComparer.Ordinal);

    public bool TryGetOffPath(string operation, out AuditCatalogEntry entry) =>
        OffPath.TryGetValue(operation, out entry!);
}

/// <summary>A classifier that returns the declared tier, or whatever a case forced.</summary>
internal sealed class FakeClassifier(AuditClassification? forced = null) : IAuditConfigService
{
    public Task<AuditClassification> ClassifyAsync(
        TenantId? tenantId, AuditCatalogEntry entry, CancellationToken cancellationToken = default) =>
        Task.FromResult(forced ?? entry.OperationClass switch
        {
            OperationClass.Must => AuditClassification.Must,
            OperationClass.Should => AuditClassification.Should,
            _ => AuditClassification.May,
        });
}

/// <summary>Records every write, and fails the ones a case tells it to.</summary>
internal sealed class RecordingAuditStore : IAuditStore
{
    public List<AuditEntryDraft> Standalone { get; } = [];

    /// <summary>Writes abandoned because the token handed in was already cancelled.</summary>
    /// <remarks>
    /// The double HONOURS its token, which a double is not obliged to do and this one has
    /// to. Every path the reconcile exists for hands it a token that is already cancelled
    /// by construction, so a store that ignored the token would record a write the real
    /// one never makes — and the case asserting the row exists would pass against a
    /// pipeline that drops every cancelled request's row.
    /// </remarks>
    public int Abandoned { get; private set; }

    public List<AuditEntryDraft> BestEffort { get; } = [];

    public int PendingWrites { get; private set; }

    /// <summary>When set, every standalone write throws — the fail-closed path.</summary>
    public bool StandaloneFails { get; init; }

    public Task WritePendingAsync(IUnitOfWork unitOfWork, CancellationToken cancellationToken = default)
    {
        PendingWrites++;
        return Task.CompletedTask;
    }

    public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Abandoned++;
            throw new OperationCanceledException(cancellationToken);
        }

        if (StandaloneFails)
        {
            throw new AuditWriteFailedException("the standalone write failed");
        }

        Standalone.Add(entry);

        return Task.CompletedTask;
    }

    public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Abandoned++;
            throw new OperationCanceledException(cancellationToken);
        }

        BestEffort.Add(entry);

        return Task.CompletedTask;
    }

    public Task WritePlatformScopeAsync(
        AuditEntryDraft entry,
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The platform-scope path has its own cases.");
}

/// <summary>A resolved context, or an unresolved one when a case needs the fourth tenant case.</summary>
internal sealed class HarnessTenantContext(bool resolved = true) : ITenantContext
{
    public bool IsResolved => resolved;

    public TenantId TenantId => LearnStack.SharedKernel.Identifiers.TenantId.From(AuditPipelineHarness.Tenant);

    public OrganizationId? OrganizationId =>
        LearnStack.SharedKernel.Identifiers.OrganizationId.From(AuditPipelineHarness.Organization);

    public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.From(AuditPipelineHarness.Actor);

    public string? CorrelationId => "00-harness-span-01";

    public string? ModuleName => "tenancy";
}

/// <summary>A clock that ADVANCES, one tick per read.</summary>
/// <remarks>
/// A fixed instant made two spellings indistinguishable: the intent's <c>DeclaredAt</c> is
/// stamped during declaration and the reconcile takes a fresh reading later, and with a
/// frozen clock every test passed whichever one the code used. The distinction is
/// load-bearing — the commit-in-doubt pair is two rows under one id, and it is legal only
/// because the timestamps differ.
/// </remarks>
internal sealed class HarnessClock(DateTimeOffset start) : IClock
{
    private int _reads;

    public DateTimeOffset UtcNow => start.AddMilliseconds(Interlocked.Increment(ref _reads));
}

/// <summary>Mints ids a case can predict, so a duplicate is visible as one.</summary>
internal sealed class HarnessGuidFactory : IGuidFactory
{
    private int _next;

    public Guid NewUuidV7() => Guid.Parse($"00000000-0000-7000-8000-{++_next:D12}");

    public Guid NewUuidV4() => NewUuidV7();
}
