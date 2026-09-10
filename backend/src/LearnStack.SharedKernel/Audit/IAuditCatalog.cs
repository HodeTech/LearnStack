namespace LearnStack.SharedKernel.Audit;

/// <summary>
/// One audited operation, as a module declared it.
/// </summary>
/// <param name="ModuleName">
/// The module this entry belongs to: the declaring
/// <see cref="IAuditCatalogSource.ModuleName"/> for a request-keyed registration, or the
/// slug's own first segment for an entry from
/// <see cref="IAuditCatalogBuilder.DeclareOffPath"/> — see <paramref name="Operation"/>.
/// </param>
/// <param name="Operation">
/// The dotted slug <c>{module}.{resource}.{verb}</c>. For a <b>request-keyed</b>
/// registration its first segment equals <paramref name="ModuleName"/>, which the
/// builder enforces rather than trusts. An entry from
/// <see cref="IAuditCatalogBuilder.DeclareOffPath"/> takes its
/// <paramref name="ModuleName"/> from the slug's own first segment instead:
/// <c>platform.admin_scope.enter</c> belongs to no module's request path, and off-path
/// entries sit outside the join in both directions
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 3 § 1</see>).
/// </param>
/// <param name="OperationType">What kind of act it is.</param>
/// <param name="OperationClass">The declared coverage tier.</param>
/// <param name="EntityType">
/// The aggregate this operation is about, or <c>null</c> when it is about none — an
/// off-path scope entry, or a security event that records no row. It is what fills
/// <c>entity_type</c> and <c>entity_id</c>, and it is declared rather than derived
/// because the slug's resource segment cannot supply it: the matrices drop the module's
/// own prefix, so <c>tenancy.feature_flag.write</c> is <c>TenantFeatureFlag</c> and
/// <c>tenancy.hostmapping.write</c> is <c>PlatformHostMapping</c> — a slug-to-type rule
/// is wrong for two shipped entities on the day it is written
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 5 § 2</see>).
/// </param>
public sealed record AuditCatalogEntry(
    string ModuleName,
    string Operation,
    OperationType OperationType,
    OperationClass OperationClass,
    Type? EntityType);

/// <summary>
/// What the catalogue knows about one request type.
/// </summary>
/// <param name="WritesNoRow">
/// The request is registered and audits nothing — how a test-only type is declared.
/// Registered-and-silent is a different answer from unregistered, and the difference is
/// the whole of the fail-closed rule.
/// </param>
/// <param name="Entries">
/// The operations this request audits, in declaration order. One request may audit
/// several: <c>ProvisionTenantCommand</c> writes two aggregate roots on one transaction
/// and the Tenancy matrix classifies both MUST.
/// </param>
public sealed record AuditRegistration(bool WritesNoRow, IReadOnlyList<AuditCatalogEntry> Entries);

/// <summary>
/// The in-process classification catalogue. Built once at startup, read on every
/// request, and <b>never</b> a database query on the request path.
/// </summary>
/// <remarks>
/// <para>
/// That is a correctness requirement rather than an optimisation. At pipeline step 3 no
/// transaction is open and <c>app.tenant_id</c> is unset, and <c>audit_config</c> carries
/// <c>ENABLE</c> + <c>FORCE</c> row security — so a lookup there returns <b>zero rows
/// silently</b>, which reads exactly like "this tenant has no overrides" and never trips
/// a fail-closed <c>catch</c>
/// (<see href="../../../../docs/decisions/0033-audit-durability-model.md">ADR-0033</see>).
/// </para>
/// <para>
/// The catalogue therefore carries the MUST floor and cannot be unavailable. A tenant's
/// <c>audit_config</c> overrides are a cached projection layered on top; a failure to
/// read one falls back to here, which is why a cache outage cannot switch auditing off.
/// </para>
/// </remarks>
public interface IAuditCatalog
{
    /// <summary>
    /// What this request type declared, or <c>false</c> if nothing did.
    /// </summary>
    /// <remarks>
    /// <c>false</c> is the rejection: the operation is refused with
    /// <see cref="AuditErrors.UnclassifiedOperation"/>. There is no exempt kind of
    /// request — every <c>IRequest&lt;Result&lt;T&gt;&gt;</c> reaching step 3 must be
    /// registered, silence included
    /// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
    /// § 6</see>).
    /// </remarks>
    bool TryGet(Type requestType, out AuditRegistration registration);

    /// <summary>
    /// What the catalogue declared for one <b>off-path</b> slug, or <c>false</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lookup an operation performed by a scope, by middleware or by a provider needs,
    /// because there is no request type for <see cref="TryGet"/> to be keyed on. It reads
    /// <see cref="IAuditCatalogBuilder.DeclareOffPath"/>'s entries and <b>only</b> those: a
    /// request-keyed slug reached by a non-request writer would be a second writer for a
    /// row the pipeline already writes, which is how one operation ends up audited twice
    /// and neither row agrees about the outcome.
    /// </para>
    /// <para>
    /// <c>false</c> is the rejection, and the caller fails closed on it: entering a
    /// cross-tenant scope whose slug no module declared is exactly the case
    /// <see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 10</see>
    /// exists to make impossible.
    /// </para>
    /// </remarks>
    bool TryGetOffPath(string operation, out AuditCatalogEntry entry);

    /// <summary>
    /// Every entry, from every module. What
    /// <c>Every_TenantOwned_Command_HasAuditCoverage</c> joins against the module
    /// matrices' <c>Operation</c> column.
    /// </summary>
    IReadOnlyCollection<AuditCatalogEntry> All { get; }
}

/// <summary>
/// How a module declares what it audits. One implementation per module, discovered from
/// DI at startup.
/// </summary>
/// <remarks>
/// <para>
/// This is the registration seam ADR-0033 named <c>IModule.RegisterAuditDefaults()</c>.
/// No <c>IModule</c> exists in this solution and no packet builds one, so the seam is
/// narrowed to the one thing Packet 9 needs
/// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 § 6</see>).
/// </para>
/// <para>
/// The catalogue is <b>code</b> and the module's <c>audit.md</c> matrix is the document;
/// one architecture test asserts they agree, in both directions and with two domains.
/// Neither is derived from the other, so neither can drift silently — and nothing has to
/// parse Markdown at build time.
/// </para>
/// </remarks>
public interface IAuditCatalogSource
{
    /// <summary>
    /// The module's short name. Every slug this source declares <b>through a
    /// request-keyed registration</b> starts with it, and it is the module whose
    /// <c>docs/modules/&lt;module&gt;/audit.md</c> the join reads.
    /// </summary>
    /// <remarks>
    /// <see cref="IAuditCatalogBuilder.DeclareOffPath"/> is exempt, and has to be: the
    /// first slug it exists for is <c>platform.admin_scope.enter</c>, which the Tenancy
    /// matrix carries because that is where a reader looks for it, while its module
    /// segment is <c>platform</c> because the scope belongs to no module's request path.
    /// Requiring the prefix there would make the operation unregistrable from the only
    /// source that could declare it — there is no <c>platform</c> module, and inventing
    /// one would owe a <c>docs/modules/platform/audit.md</c> that
    /// <see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
    /// 4 § 3</see> forbids.
    /// </remarks>
    string ModuleName { get; }

    /// <summary>Declares this module's audited operations.</summary>
    void Describe(IAuditCatalogBuilder builder);
}

/// <summary>
/// What a module may say when it declares its audit coverage.
/// </summary>
/// <remarks>
/// Every method is explicit. There is no "register everything" and no convention over
/// the type name: a request that nobody thought about must be *unregistered*, so that
/// the runtime refuses it rather than guessing a tier for it.
/// </remarks>
public interface IAuditCatalogBuilder
{
    /// <summary>Declares that this request audits an operation at MUST.</summary>
    IAuditCatalogBuilder MustAudit<TRequest>(
        string operation, OperationType operationType, Type entityType)
        where TRequest : notnull;

    /// <summary>Declares that this request audits an operation at SHOULD.</summary>
    IAuditCatalogBuilder ShouldAudit<TRequest>(
        string operation, OperationType operationType, Type entityType)
        where TRequest : notnull;

    /// <summary>Declares that this request audits an operation at MAY.</summary>
    IAuditCatalogBuilder MayAudit<TRequest>(
        string operation, OperationType operationType, Type entityType)
        where TRequest : notnull;

    /// <summary>
    /// Declares that this request is known and audits nothing.
    /// </summary>
    /// <remarks>
    /// The eight test-only request types register here, in their fixture. Registered and
    /// silent is not the same as unregistered — the first is a decision, the second is
    /// an omission, and only the second is refused.
    /// </remarks>
    IAuditCatalogBuilder Off<TRequest>()
        where TRequest : notnull;

    /// <summary>
    /// Declares an operation that is not a MediatR request at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>platform.admin_scope.enter</c>, <c>tenancy.killswitch.toggle</c>,
    /// <c>tenancy.entitlement.refresh</c> and the two tenant-assertion keys are performed
    /// by middleware, by a scope, or by a provider — never by a handler — so there is no
    /// type for a registration to be keyed on. They carry <c>(off-path)</c> in the matrix
    /// and sit outside the request-type join in both directions
    /// (<see href="../../../../docs/decisions/0044-audit-write-path.md">ADR-0044
    /// Amendment 3 § 1</see>).
    /// </para>
    /// <para>
    /// The entry's <see cref="AuditCatalogEntry.ModuleName"/> is the slug's own first
    /// segment, not the declaring source's — which is what makes
    /// <c>platform.admin_scope.enter</c> registrable from Tenancy's source, where its
    /// matrix row already lives.
    /// </para>
    /// </remarks>
    IAuditCatalogBuilder DeclareOffPath(
        string operation,
        OperationType operationType,
        OperationClass operationClass,
        Type? entityType = null);
}
