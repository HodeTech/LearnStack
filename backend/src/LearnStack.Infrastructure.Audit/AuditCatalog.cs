using System.Collections.Frozen;
using System.Text.RegularExpressions;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Infrastructure.Audit;

/// <summary>
/// Every module's declarations, merged once at startup and read on every request.
/// </summary>
/// <remarks>
/// <para>
/// <b>Built once, then immutable.</b> A singleton read by <c>AuditLogBehavior</c> at
/// pipeline step 3 on every request, so the merge happens at composition time and the
/// lookup afterwards is a dictionary hit. It is never a database query — at step 3 no
/// transaction is open and <c>app.tenant_id</c> is unset, so a query against
/// <c>audit_config</c> would return zero rows <em>silently</em>, which reads exactly like
/// "this tenant has no overrides"
/// (<see href="../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>).
/// </para>
/// <para>
/// <b>It carries the MUST floor</b>, which is why it cannot be unavailable. A tenant's
/// overrides are a cached projection layered on top, and a failure to read one falls back
/// to here.
/// </para>
/// </remarks>
public sealed class AuditCatalog : IAuditCatalog
{
    private readonly FrozenDictionary<Type, AuditRegistration> _byRequestType;
    private readonly FrozenDictionary<string, AuditCatalogEntry> _byOffPathSlug;
    private readonly IReadOnlyCollection<AuditCatalogEntry> _all;

    /// <summary>Merges every module's source. The composition root's only caller.</summary>
    public AuditCatalog(IEnumerable<IAuditCatalogSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var builder = new AuditCatalogBuilder();

        // Ordinal by module name, so the merge is deterministic whatever order DI hands
        // the sources back in. `All` feeds an architecture rule that compares it against
        // the matrices, and a rule whose input order varies between runs is a rule that
        // fails differently on a second run.
        foreach (var source in sources.OrderBy(source => source.ModuleName, StringComparer.Ordinal))
        {
            builder.Describing(source);
            source.Describe(builder);
        }

        _byRequestType = builder.BuildRegistrations();
        _byOffPathSlug = builder.BuildOffPath();
        _all = builder.BuildEntries();
    }

    /// <inheritdoc />
    public IReadOnlyCollection<AuditCatalogEntry> All => _all;

    /// <inheritdoc />
    public bool TryGet(Type requestType, out AuditRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(requestType);

        // Exact identity, no base-type or interface walk. A registration inherited from a
        // base request would classify every derived request the same way, which is the
        // guess ADR-0044 § 6 refuses: a request nobody thought about must be unregistered
        // so the runtime refuses it rather than inventing a tier for it.
        return _byRequestType.TryGetValue(requestType, out registration!);
    }

    /// <inheritdoc />
    public bool TryGetOffPath(string operation, out AuditCatalogEntry entry)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        // Off-path entries only, which is why this reads its own dictionary rather than
        // scanning `All`. A scope or a middleware that found a request-keyed slug here
        // would write a second row for an operation the pipeline already writes.
        return _byOffPathSlug.TryGetValue(operation, out entry!);
    }
}

/// <summary>
/// The builder handed to each module's <see cref="IAuditCatalogSource.Describe"/>.
/// </summary>
/// <remarks>
/// One instance for the whole merge rather than one per source, because the duplicate
/// checks are cross-source: two modules registering the same request type is exactly the
/// collision worth refusing, and a per-source builder could not see it.
/// </remarks>
internal sealed partial class AuditCatalogBuilder : IAuditCatalogBuilder
{
    private readonly Dictionary<Type, Registration> _registrations = [];
    private readonly List<AuditCatalogEntry> _offPath = [];
    private readonly Dictionary<Type, string> _declaredBy = [];

    private IAuditCatalogSource? _current;

    /// <summary>Names the source whose declarations follow.</summary>
    /// <remarks>
    /// The builder has to know: it <b>enforces</b> that a request-keyed slug's first
    /// segment is the declaring module's name rather than trusting it, and the slug alone
    /// cannot say who declared it.
    /// </remarks>
    public void Describing(IAuditCatalogSource source) => _current = source;

    /// <inheritdoc />
    public IAuditCatalogBuilder MustAudit<TRequest>(
        string operation, OperationType operationType, Type entityType)
        where TRequest : notnull =>
        Add<TRequest>(operation, operationType, OperationClass.Must, entityType);

    /// <inheritdoc />
    public IAuditCatalogBuilder ShouldAudit<TRequest>(
        string operation, OperationType operationType, Type entityType)
        where TRequest : notnull =>
        Add<TRequest>(operation, operationType, OperationClass.Should, entityType);

    /// <inheritdoc />
    public IAuditCatalogBuilder MayAudit<TRequest>(
        string operation, OperationType operationType, Type entityType)
        where TRequest : notnull =>
        Add<TRequest>(operation, operationType, OperationClass.May, entityType);

    /// <inheritdoc />
    public IAuditCatalogBuilder Off<TRequest>()
        where TRequest : notnull
    {
        var request = typeof(TRequest);

        if (_registrations.TryGetValue(request, out var existing) && existing.Entries.Count > 0)
        {
            throw new InvalidOperationException(
                $"{Source().ModuleName} registered {request.Name} as Off after declaring "
                + $"{existing.Entries.Count} operation(s) for it. Silent and audited are "
                + "different answers and a type cannot be both.");
        }

        Claim(request);
        _registrations[request] = new Registration(WritesNoRow: true, []);

        return this;
    }

    /// <inheritdoc />
    public IAuditCatalogBuilder DeclareOffPath(
        string operation,
        OperationType operationType,
        OperationClass operationClass,
        Type? entityType = null)
    {
        EnsureSlugShape(operation);

        if (entityType is not null)
        {
            EnsureConcrete(operation, entityType);
        }

        // One writer per operation. A slug a request type already registers would be
        // written twice for one act — once by the pipeline, once by the off-path writer —
        // and the matrix could only mark one of them (the fifth review of Packet 9).
        if (_registrations.Values.Any(registration =>
            registration.Entries.Any(entry =>
                string.Equals(entry.Operation, operation, StringComparison.Ordinal))))
        {
            throw new InvalidOperationException(
                $"'{operation}' is declared off-path, and a request type already registers it. "
                + "An operation has one writer: either the pipeline records it or the off-path "
                + "caller does.");
        }

        // Refused rather than shadowed. Off-path entries are reached BY SLUG — that is the
        // whole of their addressing, there being no request type — so a second declaration
        // means two modules disagree about the tier of one operation and the writer takes
        // whichever landed first. The request-keyed side has refused a double claim since
        // it was written; this side is addressed the same way and owes the same refusal.
        if (_offPath.Any(existing =>
            string.Equals(existing.Operation, operation, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"'{operation}' is declared off-path more than once. An off-path operation "
                + "is addressed by its slug, so a second declaration is two modules "
                + "disagreeing about one operation's tier with no rule for choosing.");
        }

        // The slug's OWN first segment, not the declaring source's. That is what makes
        // `platform.admin_scope.enter` registrable from Tenancy's source, where its matrix
        // row already lives — there is no `platform` module and inventing one would owe a
        // matrix ADR-0044 Amendment 4 § 3 forbids.
        _offPath.Add(new AuditCatalogEntry(
            ModuleOf(operation), operation, operationType, operationClass, entityType));

        return this;
    }

    public FrozenDictionary<Type, AuditRegistration> BuildRegistrations() =>
        _registrations.ToFrozenDictionary(
            entry => entry.Key,
            entry => new AuditRegistration(entry.Value.WritesNoRow, entry.Value.Entries));

    public FrozenDictionary<string, AuditCatalogEntry> BuildOffPath() =>
        _offPath.ToFrozenDictionary(entry => entry.Operation, StringComparer.Ordinal);

    public IReadOnlyCollection<AuditCatalogEntry> BuildEntries() =>
        [.. _registrations.Values.SelectMany(registration => registration.Entries), .. _offPath];

    private AuditCatalogBuilder Add<TRequest>(
        string operation, OperationType operationType, OperationClass operationClass, Type entityType)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(entityType);
        EnsureSlugShape(operation);
        EnsureConcrete(operation, entityType);

        var module = Source().ModuleName;

        // ENFORCED, not trusted. A slug whose module segment disagrees with its declaring
        // source is a row the matrix join will look for in the wrong file — and it would
        // look like a missing matrix row rather than a mistyped slug, which is the harder
        // failure to read.
        if (!string.Equals(ModuleOf(operation), module, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{module} declared '{operation}', whose module segment is "
                + $"'{ModuleOf(operation)}'. A request-keyed slug starts with its declaring "
                + "module's name; only DeclareOffPath is exempt, and only because the "
                + "operations it exists for belong to no module's request path.");
        }

        var request = typeof(TRequest);

        if (_registrations.TryGetValue(request, out var existing) && existing.WritesNoRow)
        {
            throw new InvalidOperationException(
                $"{module} declared '{operation}' for {request.Name}, which is already "
                + "registered Off. Silent and audited are different answers and a type "
                + "cannot be both.");
        }

        if (existing is not null
            && existing.Entries.Any(declared =>
                string.Equals(declared.Operation, operation, StringComparison.Ordinal)))
        {
            // Appended, it would be a second intent and a second row for one act.
            throw new InvalidOperationException(
                $"{module} declared '{operation}' for {request.Name} twice. One request "
                + "writes one row per audited operation.");
        }

        if (_offPath.Any(declared => string.Equals(declared.Operation, operation, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"{module} registered '{operation}' for {request.Name}, and it is already "
                + "declared off-path. An operation has one writer: either the pipeline records it "
                + "or the off-path caller does.");
        }

        Claim(request);

        var entry = new AuditCatalogEntry(module, operation, operationType, operationClass, entityType);

        if (existing is null)
        {
            _registrations[request] = new Registration(WritesNoRow: false, [entry]);
        }
        else
        {
            // Appended, so the flush order of a multi-intent request follows the order its
            // module wrote them. ProvisionTenantCommand declares two.
            existing.Entries.Add(entry);
        }

        return this;
    }

    /// <summary>
    /// Refuses an entity type no capture can carry: abstract, an interface, or open generic.
    /// </summary>
    /// <remarks>
    /// The interceptor records the concrete CLR type each tracked entry has, and the composer
    /// selects captures by that name. A registration naming a base type would bind a
    /// designation — <c>Designate</c> matches by assignability — and then find no capture of
    /// its own: <c>entity_id</c> set and both snapshots empty, silently (the fifth review of
    /// Packet 9). Refusing it here keeps the two matches the same.
    /// </remarks>
    private static void EnsureConcrete(string operation, Type entityType)
    {
        if (entityType.IsAbstract || entityType.IsInterface || entityType.ContainsGenericParameters)
        {
            throw new InvalidOperationException(
                $"'{operation}' names {entityType.Name} as its entity type, which no capture can "
                + "carry: the interceptor records the concrete type of each tracked entry. Name "
                + "the concrete aggregate.");
        }
    }

    /// <summary>
    /// Records which module owns a request type, and refuses a second claim.
    /// </summary>
    /// <remarks>
    /// Two modules registering one request type would give it two classifications and no
    /// rule for choosing — and the loser's matrix row would then have no catalogue entry,
    /// which the join reports as the OTHER module's omission.
    /// </remarks>
    private void Claim(Type request)
    {
        var module = Source().ModuleName;

        if (_declaredBy.TryGetValue(request, out var owner)
            && !string.Equals(owner, module, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{request.Name} is registered by both '{owner}' and '{module}'. A request "
                + "type belongs to one module's catalogue.");
        }

        _declaredBy[request] = module;
    }

    private IAuditCatalogSource Source() =>
        _current ?? throw new InvalidOperationException(
            "The builder was used outside a source's Describe. Every registration belongs "
            + "to a declaring module, because the module segment of every request-keyed "
            + "slug is checked against it.");

    private static string ModuleOf(string operation) => operation[..operation.IndexOf('.')];

    /// <summary>
    /// Refuses a slug that is not <c>{module}.{resource}.{verb}</c>.
    /// </summary>
    /// <remarks>
    /// The shape is fixed by ADR-0044 § 6 — three segments, lowercase, snake_case where a
    /// segment is multi-word. It is checked here because the slug is written by hand in
    /// two places, the catalogue and the matrix, and a shape mismatch between them reads
    /// as a missing row rather than a typo.
    /// </remarks>
    private static void EnsureSlugShape(string operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);

        if (!SlugPattern().IsMatch(operation))
        {
            throw new InvalidOperationException(
                $"'{operation}' is not an audit operation slug. The shape is "
                + "{module}.{resource}.{verb} — three segments, lowercase, snake_case "
                + "where a segment is multi-word (ADR-0044 § 6).");
        }
    }

    /// <remarks>
    /// Anchored with <c>\z</c> rather than <c>$</c>. In .NET — with or without
    /// <c>Multiline</c> — <c>$</c> also matches immediately before a single trailing
    /// newline, so <c>"tenancy.tenant.create\n"</c> passed this gate and travelled
    /// through <c>AuditCatalogEntry.Operation</c> and <c>AuditIntent</c> into
    /// <c>audit_log.operation</c>. The join would then report a missing matrix row for a
    /// slug that reads correctly in every log line, which is exactly the failure the shape
    /// check exists to prevent.
    /// </remarks>
    [GeneratedRegex("^[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*\\.[a-z][a-z0-9_]*\\z")]
    private static partial Regex SlugPattern();

    private sealed record Registration(bool WritesNoRow, List<AuditCatalogEntry> Entries);
}
