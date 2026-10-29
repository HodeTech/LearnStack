using System.Collections.Immutable;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Tools.Seeder;

/// <summary>One fixed origin completion purpose; never a pristine or human read source.</summary>
internal sealed class FixedSeedCompletionSource : SeedCompletionReadWitness.MintingSource
{
    private readonly IOperationInvocationContext _execution;
    private readonly IOperationPolicyCatalog _catalog;
    private readonly ITenantContext _context;
    private readonly IUnitOfWork _unit;
    private readonly IIdentityMutationLocks _locks;
    private readonly SeedOperationSource? _seedSource;
    private readonly ImmutableDictionary<TenantId, ImmutableDictionary<Type, object>> _declarations;
    private OperationInvocation? _invocation;
    private IdentityMutationLockPlan? _plan;
    private ImmutableDictionary<Type, object>? _captured;
    private SeedAdministrationOriginPreparedTarget? _prepared;
    private bool _attempted;

    public FixedSeedCompletionSource(IOperationInvocationContext execution, IOperationPolicyCatalog catalog,
        ITenantContext context, IUnitOfWork unit, IIdentityMutationLocks locks, IEnumerable<ITrustedOperationSource> sources)
    {
        _execution = execution;
        _catalog = catalog;
        _context = context;
        _unit = unit;
        _locks = locks;
        _seedSource = sources.OfType<SeedOperationSource>().SingleOrDefault();
        _declarations = CaptureDeclarations();
    }

    internal SeedCompletionReadWitness? Capture(AdoptSeedAdministrationOriginCommand request,
        SeedAdministrationOriginPreparedTarget prepared, IdentityMutationLockPlan plan)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(prepared);
        ArgumentNullException.ThrowIfNull(plan);
        if (_attempted) return null;
        _attempted = true;
        var invocation = _execution.CurrentInvocation;
        if (!HasInvocation(invocation) || !ReferenceEquals(invocation!.Operation, request)
            || !_unit.HasActiveTransaction || _unit.IsRollbackOnly || _unit.Mode != TransactionMode.ReadWrite
            || _unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || _unit is not IOperationReadScopeView { IsTenantWideRead: false }
            || _unit.Transaction is not { } transaction
            || _unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != invocation.TenantId || announcement.OrganizationId is not null
            || !_declarations.TryGetValue(_context.TenantId, out var declaration)
            || !Matches(request, prepared, plan)) return null;
        _locks.RequireCurrent(plan);
        _invocation = invocation;
        _plan = plan;
        _captured = declaration;
        _prepared = prepared;
        return ForCompletionRead(invocation, plan, _locks, declaration, prepared, _unit, transaction, announcement.Generation);
    }

    protected override bool IsCurrent(SeedCompletionReadWitness witness) =>
        ReferenceEquals(witness.Invocation, _invocation) && ReferenceEquals(witness.Plan, _plan)
        && HasInvocation(witness.Invocation) && _captured is { } declaration && _prepared is { } prepared
        && ReferenceEquals(witness.GetPreparedTarget<SeedAdministrationOriginPreparedTarget>(), prepared)
        && ReferenceEquals(witness.GetDeclaration<SeedTenantDeclaration>(), declaration[typeof(SeedTenantDeclaration)])
        && ReferenceEquals(witness.GetDeclaration<SeedCustomizationDeclaration>(), declaration[typeof(SeedCustomizationDeclaration)])
        && ReferenceEquals(witness.GetDeclaration<SeedEducationDeclaration>(), declaration[typeof(SeedEducationDeclaration)])
        && ReferenceEquals(witness.GetDeclaration<SeedIdentityCompletionDeclaration>(), declaration[typeof(SeedIdentityCompletionDeclaration)])
        && witness.Invocation.Operation is AdoptSeedAdministrationOriginCommand request && Matches(request, prepared, witness.Plan);

    private bool HasInvocation(OperationInvocation? invocation) =>
        invocation is { IsActive: true, EntryKind: OperationEntryKind.Application }
        && ReferenceEquals(invocation, _execution.CurrentInvocation) && ReferenceEquals(invocation.Context, _context)
        && invocation.Operation.GetType() == typeof(AdoptSeedAdministrationOriginCommand)
        && _context.IsResolved && _context.OrganizationId is null && _context.HostScope is null
        && _context.Origin == TenantContextOrigin.Ambient && (_context.UserId is null || _context.UserId == UserId.SystemActor)
        && _catalog.TryGet(typeof(AdoptSeedAdministrationOriginCommand), out var policy)
        && ReferenceEquals(policy, invocation.Policy) && policy.Kind == OperationPolicyKind.FiniteTrusted
        && policy.AllowedSources.Length == 1 && policy.AllowedSources[0] == AuthorizationSourceKind.Seed
        && invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.Seed } source
        && _seedSource is not null && _seedSource.Owns(source, invocation.Operation.GetType(), _context);

    private bool Matches(AdoptSeedAdministrationOriginCommand request, SeedAdministrationOriginPreparedTarget prepared,
        IdentityMutationLockPlan plan) => SeedAdministrationOriginManifest.Find(_context.TenantId) is { } declaration
        && prepared.TenantId == declaration.TenantId && prepared.TenantSlug == declaration.TenantSlug
        && prepared.TenantVersion > 0 && prepared.TenantVersion == request.ExpectedVersion
        && prepared.Marker is 0 or 1 && prepared.DefaultOrganizationId is not null
        && plan.TenantId == declaration.TenantId && plan.UserIds.IsEmpty && plan.TenantMode == IdentityTenantLockMode.Exclusive
        && (prepared.Mapping is null ? prepared.Marker == 0
            : prepared.Mapping.Host == declaration.Host && prepared.Mapping.TenantId == declaration.TenantId
                && prepared.Mapping.OrganizationId is null && prepared.Mapping.IsActive && prepared.Mapping.IsPubliclyLive);

    private static ImmutableDictionary<TenantId, ImmutableDictionary<Type, object>> CaptureDeclarations()
    {
        var tenancy = new FixedSeedTenantContinuationSource();
        var content = new FixedSeedContentContinuationSource();
        var manifest = IdentityFoundationManifest.Version1;
        return SeedData.All.ToImmutableDictionary(tenant => tenant.TenantId, tenant =>
        {
            var tenantPins = tenancy.GetDeclaration(new(SeedTenantContinuationOperation.IdentityFoundation, tenant.TenantId))
                ?? throw new InvalidOperationException("The fixed completion topology declaration is unavailable.");
            var customization = ((ISeedCustomizationContinuationSource)content).ForTenant(tenant.TenantId)
                ?? throw new InvalidOperationException("The fixed completion content declaration is unavailable.");
            var education = ((ISeedEducationContinuationSource)content).ForTenant(tenant.TenantId)
                ?? throw new InvalidOperationException("The fixed completion curriculum declaration is unavailable.");
            var roles = SeedData.IdentityRoles.Where(role => role.Tenant.TenantId == tenant.TenantId)
                .Select(role => new FixedSeedRoleDeclaration(role.RoleId, role.BuiltInKey, role.Scope,
                    [.. manifest.FindTemplate(role.BuiltInKey, role.Scope)!.Grants.Select(grant => new PermissionDefinition(grant.Scope, grant.Key))]))
                .ToImmutableArray();
            var members = SeedData.IdentityMemberships.Where(member => member.Identity.Tenant.TenantId == tenant.TenantId)
                .Select(member => new FixedSeedMembershipDeclaration(member.MembershipId, member.Identity.UserId,
                    member.OrganizationId, member.Role.RoleId, member.IsFirstAdmin)).ToImmutableArray();
            var identity = new SeedIdentityCompletionDeclaration(tenant.TenantId, manifest.Version, manifest.Fingerprint, roles, members);
            return ImmutableDictionary<Type, object>.Empty
                .Add(typeof(SeedTenantDeclaration), tenantPins)
                .Add(typeof(SeedCustomizationDeclaration), customization)
                .Add(typeof(SeedEducationDeclaration), education)
                .Add(typeof(SeedIdentityCompletionDeclaration), identity);
        });
    }
}
