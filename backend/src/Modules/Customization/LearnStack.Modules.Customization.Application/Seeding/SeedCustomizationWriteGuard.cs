using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using System.Data.Common;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Customization.Application.Seeding;

public interface ISeedCustomizationWriteGuard
{
    Error Failure { get; }
    Task<bool> PrepareAsync(object request, CancellationToken cancellationToken);
    bool IsCurrent(object request);
}

/// <summary>One complete Tenant plan, then module locks and fresh ordinary-scope prerequisites.</summary>
public sealed class SeedCustomizationWriteGuard(ISeedCustomizationContinuationSource source,
    ISeedCustomizationState state, ISeedTenantContinuationGuard tenancy, ISeedIdentityStageReader identity,
    IIdentityMutationLocks locks, IOperationInvocationContext execution, ITenantContext context, IUnitOfWork unit)
    : ISeedCustomizationWriteGuard, ISeedCustomizationPrerequisiteReader
{
    public Error Failure { get; private set; } = new(new LocalizedMessage("lockey_business_rule_violation"));

    private object? _request;
    private OperationInvocation? _invocation;
    private SeedCustomizationDeclaration? _declaration;
    private IdentityMutationLockPlan? _plan;
    private DbTransaction? _transaction;
    private long _generation;
    private bool _started;
    private bool _event;

    public async Task<bool> PrepareAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (_started || !HasInvocation(execution.CurrentInvocation, request) || !HasOwner()
            || context.OrganizationId is not null) return false;
        _event = execution.CurrentInvocation!.TrustedSource!.SourceKind == AuthorizationSourceKind.IntegrationEvent;
        _declaration = _event ? null : source.ForTenant(context.TenantId);
        if (!_event && (_declaration is null || _declaration.TenantId != context.TenantId || !Matches(request, _declaration))) return false;
        if (_event && request is not (InitializeTenantContentTypeDefaultCommand or InitializeTenantLevelTaxonomyDefaultCommand)) return false;
        _started = true;
        _request = request;
        _invocation = execution.CurrentInvocation;
        _transaction = unit.Transaction;
        _generation = unit.GetTenantContextAnnouncement(_transaction)!.Generation;
        _plan = new IdentityMutationLockPlan([], context.TenantId,
            _event ? IdentityTenantLockMode.Shared : IdentityTenantLockMode.Exclusive);
        await locks.AcquireAsync(_plan, cancellationToken);
        if (!IsCurrent(request)) return false;
        var (taxonomy, key, id, registration) = Target(request, _declaration);
        await state.PrepareAsync(taxonomy, key, id, registration, cancellationToken);
        if (!IsCurrent(request)) return false;
        // Event initialization retains create-only concept semantics. It never
        // receives the fixed Seed source, Role-history admission or a read hatch.
        if (_event) return true;
        var pristine = await tenancy.AllowsContentContinuationAsync(context.TenantId, requiresTheme: false, cancellationToken);
        if (!IsCurrent(request)) return false;
        if (!pristine) return ChangedState();
        var noIdentity = await identity.HasNoRoleHistoryAsync(_plan, cancellationToken);
        if (!IsCurrent(request)) return false;
        if (!noIdentity) return ChangedState();
        var content = await state.ReadContentTypesAsync(cancellationToken);
        if (!IsCurrent(request)) return false;
        var taxonomies = await state.ReadTaxonomiesAsync(cancellationToken);
        if (!IsCurrent(request)) return false;
        return AllowsStage(request, _declaration!, content, taxonomies) || ChangedState();
    }

    private bool ChangedState()
    {
        Failure = new Error(new LocalizedMessage("lockey_concurrency_conflict"));
        return false;
    }

    public bool IsCurrent(object request)
    {
        if (!_started || !ReferenceEquals(_request, request) || !ReferenceEquals(_invocation, execution.CurrentInvocation)
            || !HasInvocation(_invocation, request) || !HasOwner() || !ReferenceEquals(_transaction, unit.Transaction)
            || unit.GetTenantContextAnnouncement(_transaction)?.Generation != _generation
            || !_event && (!ReferenceEquals(source.ForTenant(context.TenantId), _declaration) || !Matches(request, _declaration!)))
        {
            if (unit.HasActiveTransaction) unit.MarkRollbackOnly();
            return false;
        }
        locks.RequireCurrent(_plan!);
        return true;
    }

    public async Task<bool> IsCompleteAsync(CancellationToken cancellationToken = default)
    {
        var invocation = execution.CurrentInvocation;
        if (!HasOwner() || invocation is not { IsActive: true, EntryKind: OperationEntryKind.Application }
            || !ReferenceEquals(invocation.Context, context) || invocation.Policy.Kind != OperationPolicyKind.FiniteTrusted
            || invocation.Policy.RequestType != invocation.Operation.GetType()
            || invocation.TrustedSource is not { SourceKind: AuthorizationSourceKind.Seed } trusted
            || !trusted.Matches(invocation.Operation.GetType(), context)
            || invocation.Policy.Id is not ("education.seed.create_course" or "education.seed.add_course_translation"
                or "education.seed.publish_course" or "education.seed.create_lesson" or "education.seed.add_lesson_translation"
                or "education.seed.publish_lesson")
            || source.ForTenant(context.TenantId) is not { } declaration) return false;
        var transaction = unit.Transaction;
        var announcement = unit.GetTenantContextAnnouncement(transaction);
        var content = await state.ReadContentTypesAsync(cancellationToken);
        if (!invocation.IsActive || !ReferenceEquals(invocation, execution.CurrentInvocation) || !HasOwner()
            || !ReferenceEquals(unit.Transaction, transaction) || unit.GetTenantContextAnnouncement(transaction) != announcement
            || !ReferenceEquals(source.ForTenant(context.TenantId), declaration)) return false;
        var taxonomies = await state.ReadTaxonomiesAsync(cancellationToken);
        return invocation.IsActive && ReferenceEquals(invocation, execution.CurrentInvocation) && HasOwner()
            && ReferenceEquals(unit.Transaction, transaction) && unit.GetTenantContextAnnouncement(transaction) == announcement
            && ReferenceEquals(source.ForTenant(context.TenantId), declaration)
            && content.Count == 2 && taxonomies.Count == 2
            && Content(content, declaration.BuiltInContentType, declaration.TenantId, active: true)
            && Content(content, declaration.ContentType, declaration.TenantId, active: true)
            && Taxonomy(taxonomies, declaration.BuiltInTaxonomy, declaration.TenantId, active: true)
            && Taxonomy(taxonomies, declaration.Taxonomy, declaration.TenantId, active: true);
    }

    private bool HasOwner() => context.IsResolved && context.HostScope is null && unit.HasActiveTransaction && !unit.IsRollbackOnly
        && unit.Mode == TransactionMode.ReadWrite && unit is not IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
        && unit.GetTenantContextAnnouncement(unit.Transaction) is { Generation: > 0 } announcement
        && announcement.TenantId == context.TenantId && announcement.OrganizationId == context.OrganizationId
        && unit is not IOperationReadScopeView { IsTenantWideRead: true };

    private bool HasInvocation(OperationInvocation? invocation, object request) => invocation is
    { IsActive: true, EntryKind: OperationEntryKind.Application }
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && invocation.Policy.RequestType == request.GetType() && invocation.Policy.Kind == OperationPolicyKind.FiniteTrusted
        && invocation.TrustedSource is { } trusted && trusted.Matches(request.GetType(), context)
        && invocation.Policy.Id == Policy(request)
        && (trusted.SourceKind == AuthorizationSourceKind.Seed && invocation.Policy.AllowedSources.Contains(AuthorizationSourceKind.Seed)
            || trusted.SourceKind == AuthorizationSourceKind.IntegrationEvent
                && request is InitializeTenantContentTypeDefaultCommand or InitializeTenantLevelTaxonomyDefaultCommand
                && invocation.Policy.AllowedSources.Contains(AuthorizationSourceKind.IntegrationEvent));

    private static string? Policy(object request) => request switch
    {
        InitializeTenantContentTypeDefaultCommand => "customization.default.initialize_content_type",
        InitializeTenantLevelTaxonomyDefaultCommand => "customization.default.initialize_taxonomy",
        RegisterTenantContentTypeCommand => "customization.seed.register_content_type",
        PublishTenantContentTypeCommand => "customization.seed.publish_content_type",
        RegisterTenantLevelTaxonomyCommand => "customization.seed.register_taxonomy",
        PublishTenantLevelTaxonomyCommand => "customization.seed.publish_taxonomy",
        _ => null,
    };

    private static bool Matches(object request, SeedCustomizationDeclaration declaration) => request switch
    {
        InitializeTenantContentTypeDefaultCommand or InitializeTenantLevelTaxonomyDefaultCommand => true,
        RegisterTenantContentTypeCommand value => value.ContentTypeId == declaration.ContentType.Id
            && value.Key == declaration.ContentType.Key && value.SchemaVersion == declaration.ContentType.SchemaVersion
            && Map(value.DisplayName, declaration.ContentType.DisplayName) && value.JsonSchema == declaration.ContentType.JsonSchema
            && value.RendererKey == declaration.ContentType.RendererKey,
        PublishTenantContentTypeCommand value => value.ContentTypeId == declaration.ContentType.Id && value.RequireNoIncumbent,
        RegisterTenantLevelTaxonomyCommand value => value.TaxonomyId == declaration.Taxonomy.Id
            && value.Key == declaration.Taxonomy.Key && value.SchemaVersion == declaration.Taxonomy.SchemaVersion
            && Map(value.DisplayName, declaration.Taxonomy.DisplayName) && value.Items.Count == declaration.Taxonomy.Items.Length
            && value.Items.Select((item, index) => item.Key == declaration.Taxonomy.Items[index].Key
                && item.Sort == declaration.Taxonomy.Items[index].Sort && item.Metadata == declaration.Taxonomy.Items[index].Metadata
                && Map(item.DisplayName, declaration.Taxonomy.Items[index].DisplayName)).All(value => value),
        PublishTenantLevelTaxonomyCommand value => value.TaxonomyId == declaration.Taxonomy.Id && value.RequireNoIncumbent,
        _ => false,
    };

    private static (bool Taxonomy, string Key, Guid? Id, bool Registration) Target(object request, SeedCustomizationDeclaration? declaration) => request switch
    {
        InitializeTenantContentTypeDefaultCommand => (false, BuiltInCustomizations.Card.Key, null, false),
        InitializeTenantLevelTaxonomyDefaultCommand => (true, BuiltInCustomizations.Plain.Key, null, false),
        RegisterTenantContentTypeCommand => (false, declaration!.ContentType.Key, declaration.ContentType.Id, true),
        PublishTenantContentTypeCommand => (false, declaration!.ContentType.Key, declaration.ContentType.Id, false),
        RegisterTenantLevelTaxonomyCommand => (true, declaration!.Taxonomy.Key, declaration.Taxonomy.Id, true),
        PublishTenantLevelTaxonomyCommand => (true, declaration!.Taxonomy.Key, declaration.Taxonomy.Id, false),
        _ => throw new InvalidOperationException("Fixed content operation is unavailable."),
    };

    private static bool AllowsStage(object request, SeedCustomizationDeclaration declaration,
        IReadOnlyList<TenantContentType> content, IReadOnlyList<TenantLevelTaxonomy> taxonomies)
    {
        if (content.Any(row => row.Key != declaration.BuiltInContentType.Key && row.Key != declaration.ContentType.Key)
            || taxonomies.Any(row => row.Key != declaration.BuiltInTaxonomy.Key && row.Key != declaration.Taxonomy.Key)) return false;
        var builtInContent = Content(content, declaration.BuiltInContentType, declaration.TenantId, active: true);
        var builtInTaxonomy = Taxonomy(taxonomies, declaration.BuiltInTaxonomy, declaration.TenantId, active: true);
        var noContent = !content.Any(row => row.Key == declaration.ContentType.Key || row.Id.Value == declaration.ContentType.Id);
        var noTaxonomy = !taxonomies.Any(row => row.Key == declaration.Taxonomy.Key || row.Id.Value == declaration.Taxonomy.Id);
        return request switch
        {
            InitializeTenantContentTypeDefaultCommand => noContent && noTaxonomy
                && (taxonomies.Count == 0 || builtInTaxonomy && taxonomies.Count == 1)
                && (content.Count == 0 || builtInContent && content.Count == 1),
            InitializeTenantLevelTaxonomyDefaultCommand => noContent && noTaxonomy && builtInContent && content.Count == 1
                && (taxonomies.Count == 0 || builtInTaxonomy && taxonomies.Count == 1),
            RegisterTenantContentTypeCommand => builtInContent && builtInTaxonomy && noContent && noTaxonomy,
            PublishTenantContentTypeCommand => builtInContent && builtInTaxonomy && noTaxonomy
                && Content(content, declaration.ContentType, declaration.TenantId, active: false),
            RegisterTenantLevelTaxonomyCommand => builtInContent && builtInTaxonomy && noTaxonomy
                && Content(content, declaration.ContentType, declaration.TenantId, active: true),
            PublishTenantLevelTaxonomyCommand => builtInContent && builtInTaxonomy
                && Content(content, declaration.ContentType, declaration.TenantId, active: true)
                && Taxonomy(taxonomies, declaration.Taxonomy, declaration.TenantId, active: false),
            _ => false,
        };
    }

    private static bool Content(IReadOnlyList<TenantContentType> rows, SeedContentTypeDeclaration expected, TenantId tenant, bool active)
    {
        var found = rows.Where(row => row.Key == expected.Key || row.Id.Value == expected.Id).ToArray();
        return found.Length == 1 && found[0] is { } row && (expected.Id is null || row.Id.Value == expected.Id)
            && row.TenantId == tenant && row.Key == expected.Key && row.SchemaVersion == expected.SchemaVersion && row.SchemaRevision == 0
            && row.Status == (active ? CustomizationStatus.Active : CustomizationStatus.Draft)
            && Pristine(row, active ? 1 : 0) && Json(row.DisplayName.ToJson(), JsonSerializer.Serialize(expected.DisplayName))
            && Json(row.JsonSchema, expected.JsonSchema) && row.RendererKey == expected.RendererKey;
    }

    private static bool Taxonomy(IReadOnlyList<TenantLevelTaxonomy> rows, SeedTaxonomyDeclaration expected, TenantId tenant, bool active)
    {
        var found = rows.Where(row => row.Key == expected.Key || row.Id.Value == expected.Id).ToArray();
        return found.Length == 1 && found[0] is { } row && (expected.Id is null || row.Id.Value == expected.Id)
            && row.TenantId == tenant && row.Key == expected.Key && row.SchemaVersion == expected.SchemaVersion
            && row.SchemaRevision == expected.Items.Length && row.Status == (active ? CustomizationStatus.Active : CustomizationStatus.Draft)
            && Pristine(row, expected.Items.Length + (active ? 1 : 0))
            && Json(row.DisplayName.ToJson(), JsonSerializer.Serialize(expected.DisplayName)) && row.Items.Count == expected.Items.Length
            && row.Items.All(item => expected.Items.Any(expectedItem => item.Key == expectedItem.Key && item.Sort == expectedItem.Sort
                && Json(item.DisplayName.ToJson(), JsonSerializer.Serialize(expectedItem.DisplayName)) && Json(item.Metadata, expectedItem.Metadata)));
    }

    private static bool Pristine<TId>(AuditableEntity<TId> row, long version)
        where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId> => row.Version == version
        && row.CreatedAt != default && row.CreatedBy == UserId.SystemActor && row.DeletedAt is null && row.DeletedBy is null
        && (version == 0 ? row.UpdatedAt is null && row.UpdatedBy is null : row.UpdatedAt is { } at && at != default && row.UpdatedBy == UserId.SystemActor);

    private static bool Map(IReadOnlyDictionary<string, string> left, ImmutableDictionary<string, string> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && pair.Value == value);

    private static bool Json(string? left, string? right)
    {
        if (left is null || right is null) return left == right;
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return false; }
    }
}
