using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Tools.Seeder;

/// <summary>One-use source for exactly the eight fixed consumers, with independently copied v1 pins.</summary>
internal sealed class FixedSeedPrerequisiteSource : SeedPrerequisiteReadWitness.MintingSource
{
    private readonly IOperationInvocationContext _execution;
    private readonly IOperationPolicyCatalog _catalog;
    private readonly ITenantContext _context;
    private readonly IUnitOfWork _unit;
    private readonly IIdentityMutationLocks _locks;
    private readonly SeedOperationSource? _seedSource;
    private readonly ImmutableArray<FixedRequest> _requests;
    private OperationInvocation? _invocation;
    private IdentityMutationLockPlan? _plan;
    private FixedRequest? _captured;
    private bool _attempted;

    public FixedSeedPrerequisiteSource(IOperationInvocationContext execution, IOperationPolicyCatalog catalog,
        ITenantContext context, IUnitOfWork unit, IIdentityMutationLocks locks, IEnumerable<ITrustedOperationSource> sources)
    {
        _execution = execution;
        _catalog = catalog;
        _context = context;
        _unit = unit;
        _locks = locks;
        _seedSource = sources.OfType<SeedOperationSource>().SingleOrDefault();
        _requests = CaptureFixedRequests();
    }

    internal SeedPrerequisiteReadWitness? Capture(object request, IdentityMutationLockPlan plan)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(plan);
        if (_attempted) return null;
        _attempted = true;
        var invocation = _execution.CurrentInvocation;
        if (!HasInvocation(invocation) || !ReferenceEquals(invocation!.Operation, request)
            || !_unit.HasActiveTransaction || _unit.IsRollbackOnly || _unit.Mode != TransactionMode.ReadWrite
            || _unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || _unit.Transaction is not { } transaction
            || _unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != invocation.TenantId || announcement.OrganizationId != invocation.OrganizationId)
            return null;
        var captured = _requests.SingleOrDefault(value => Matches(value, invocation, plan));
        if (captured is null) return null;
        _locks.RequireCurrent(plan);
        _invocation = invocation;
        _plan = plan;
        _captured = captured;
        return ForPrerequisiteRead(invocation, plan, _locks, captured.Declaration, _unit, transaction, announcement.Generation);
    }

    protected override bool IsCurrent(SeedPrerequisiteReadWitness witness) =>
        ReferenceEquals(witness.Invocation, _invocation) && ReferenceEquals(witness.Plan, _plan)
        && _captured is { } captured && ReferenceEquals(witness.GetDeclaration<FixedSeedPrerequisiteDeclaration>(), captured.Declaration)
        && HasInvocation(witness.Invocation) && Matches(captured, witness.Invocation, witness.Plan);

    private bool HasInvocation(OperationInvocation? invocation) =>
        invocation is { IsActive: true, EntryKind: OperationEntryKind.Application }
        && ReferenceEquals(invocation, _execution.CurrentInvocation) && ReferenceEquals(invocation.Context, _context)
        && _context.IsResolved && _context.HostScope is null && _context.Origin == TenantContextOrigin.Ambient
        && (_context.UserId is null || _context.UserId == UserId.SystemActor)
        && _catalog.TryGet(invocation.Operation.GetType(), out var policy) && ReferenceEquals(policy, invocation.Policy)
        && policy.Kind == OperationPolicyKind.FiniteTrusted && policy.AllowedSources.Length == 1
        && policy.AllowedSources[0] == AuthorizationSourceKind.Seed
        && invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.Seed } source
        && _seedSource is not null && _seedSource.Owns(source, invocation.Operation.GetType(), _context);

    private static bool Matches(FixedRequest pin, OperationInvocation invocation, IdentityMutationLockPlan plan) =>
        pin.Request.GetType() == invocation.Operation.GetType() && pin.Request.Equals(invocation.Operation)
        && pin.Declaration.TenantId == invocation.TenantId && pin.OrganizationId == invocation.OrganizationId
        && plan.TenantId == pin.Declaration.TenantId && plan.TenantMode == IdentityTenantLockMode.Exclusive
        && plan.UserIds.SequenceEqual(pin.Users);

    private static ImmutableArray<FixedRequest> CaptureFixedRequests()
    {
        var manifest = IdentityFoundationManifest.Version1;
        var requests = ImmutableArray.CreateBuilder<FixedRequest>();
        foreach (var tenant in SeedData.All)
        {
            // Recreate every record and collection; later observations never consult mutable runner inputs.
            var roles = SeedData.IdentityRoles.Where(role => role.Tenant.TenantId == tenant.TenantId)
                .Select(role => new FixedSeedRoleDeclaration(role.RoleId, role.BuiltInKey, role.Scope,
                    [.. manifest.FindTemplate(role.BuiltInKey, role.Scope)!.Grants.Select(grant => new PermissionDefinition(grant.Scope, grant.Key))]))
                .ToImmutableArray();
            var members = SeedData.IdentityMemberships.Where(member => member.Identity.Tenant.TenantId == tenant.TenantId)
                .OrderByDescending(member => member.IsFirstAdmin)
                .Select(member => new FixedSeedMembershipDeclaration(member.MembershipId, member.Identity.UserId,
                    member.OrganizationId, member.Role.RoleId, member.IsFirstAdmin)).ToImmutableArray();
            if (roles.Length != 8 || members.Length != 2 || roles.Select(role => role.Id).Distinct().Count() != 8
                || members.Select(member => member.Id).Distinct().Count() != 2
                || !members[0].IsFirstAdmin || members[1].IsFirstAdmin)
                throw new InvalidOperationException("Fixed seed prerequisite declaration is inconsistent.");
            var declaration = new FixedSeedPrerequisiteDeclaration(tenant.TenantId, manifest.Version, manifest.Fingerprint,
                FixedSeedPrerequisiteStage.Tenancy, 0, roles, members);
            requests.Add(new(new CreateOrganizationCommand(tenant.SecondOrganization.OrganizationId,
                tenant.SecondOrganization.Slug, tenant.SecondOrganization.DisplayName), null, [], declaration));
            requests.Add(new(new MapHostToTenantCommand(tenant.Host,
                tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId : null, true, true), null, [], declaration));
            if (tenant.Curriculum is { } curriculum)
            {
                var locales = curriculum.Locales.OrderByDescending(locale => locale.IsDefault).ThenBy(locale => locale.Sort).ToArray();
                for (var index = 0; index < locales.Length; index++)
                {
                    var locale = locales[index];
                    requests.Add(new(new AddTenantLocaleCommand(1L + index, locale.Locale, locale.IsEnabled, locale.IsDefault, locale.Sort),
                        null, [], declaration));
                }
                requests.Add(new(new SetTenantBrandingCommand(curriculum.Theme.Id, curriculum.Theme.Value, null), null, [], declaration));
            }
            for (var index = 0; index < roles.Length; index++)
            {
                var role = roles[index];
                requests.Add(new(new InitializeSeedRoleCommand(role.Id, role.BuiltInKey, role.Scope, manifest.Version,
                    manifest.RoleFingerprint(tenant.TenantId, role.Id, role.BuiltInKey, role.Scope)), null, [],
                    declaration with { Stage = FixedSeedPrerequisiteStage.Role, TargetIndex = index }));
            }
            for (var index = 0; index < members.Length; index++)
            {
                var member = members[index];
                var scope = member.OrganizationId is null ? PermissionScope.Tenant : PermissionScope.Organization;
                var fingerprint = manifest.MembershipFingerprint(tenant.TenantId, member.OrganizationId, member.Id, member.UserId, member.RoleId);
                object request = member.IsFirstAdmin
                    ? new CreateFirstSeedAdminMembershipCommand(member.Id, member.UserId, member.RoleId, manifest.Version, fingerprint)
                    : new CreateSeedMembershipCommand(member.Id, member.UserId, member.RoleId, scope, manifest.Version, fingerprint);
                requests.Add(new(request, member.OrganizationId, [member.UserId],
                    declaration with { Stage = FixedSeedPrerequisiteStage.Membership, TargetIndex = index }));
                var profile = SeedData.IdentityMemberships.Single(value => value.MembershipId == member.Id);
                requests.Add(new(new SetSeedMembershipProfileCommand(member.Id, member.UserId, member.RoleId, scope,
                    profile.DisplayName, profile.PreferredLocale, manifest.Version,
                    manifest.ProfileFingerprint(tenant.TenantId, member.OrganizationId, member.Id, profile.DisplayName, profile.PreferredLocale)),
                    member.OrganizationId, [member.UserId], declaration with { Stage = FixedSeedPrerequisiteStage.Profile, TargetIndex = index }));
            }
        }
        return requests.ToImmutable();
    }

    private sealed record FixedRequest(object Request, OrganizationId? OrganizationId, ImmutableArray<UserId> Users,
        FixedSeedPrerequisiteDeclaration Declaration);
}
