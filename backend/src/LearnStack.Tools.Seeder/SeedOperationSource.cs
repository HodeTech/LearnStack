using System.Collections.Frozen;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Tools.Seeder;

/// <summary>The per-act composition owns this closed seed capability and its disposal.</summary>
internal sealed class SeedOperationSource : TrustedOperationDescriptor.MintingSource, ITrustedOperationSource
{
    private static readonly FrozenSet<Type> Requests = new[]
    {
        typeof(GetTenantSeedStateQuery), typeof(GetOrganizationSeedStateQuery), typeof(GetHostMappingSeedStateQuery), typeof(GetSettingSeedStateQuery),
        typeof(GetContentTypeSeedStateQuery), typeof(GetTaxonomySeedStateQuery), typeof(GetActiveContentTypeSeedRevisionQuery), typeof(GetActiveTaxonomySeedRevisionQuery),
        typeof(GetCourseSeedStateQuery), typeof(GetLessonSeedStateQuery), typeof(GetRoleSeedStateQuery), typeof(GetMembershipSeedStateQuery),
        typeof(InitializeSeedRoleCommand), typeof(CreateFirstSeedAdminMembershipCommand), typeof(CreateSeedMembershipCommand), typeof(SetSeedMembershipProfileCommand),
        typeof(ProvisionTenantCommand), typeof(CreateOrganizationCommand), typeof(MapHostToTenantCommand), typeof(AddTenantLocaleCommand),
        typeof(AdoptSeedAdministrationOriginCommand),
        typeof(SetDefaultTenantLocaleCommand), typeof(SetTenantBrandingCommand), typeof(RegisterTenantContentTypeCommand), typeof(PublishTenantContentTypeCommand),
        typeof(RegisterTenantLevelTaxonomyCommand), typeof(PublishTenantLevelTaxonomyCommand), typeof(InitializeTenantContentTypeDefaultCommand), typeof(InitializeTenantLevelTaxonomyDefaultCommand),
        typeof(CreateCourseCommand), typeof(AddCourseTranslationCommand), typeof(PublishCourseCommand), typeof(CreateLessonCommand), typeof(AddLessonTranslationCommand), typeof(PublishLessonCommand),
    }.ToFrozenSet();

    private readonly ITenantContext _context;
    private readonly bool _resolved;
    private readonly TenantId? _tenant;
    private readonly OrganizationId? _organization;
    private readonly UserId? _actor;
    private readonly HashSet<TrustedOperationDescriptor> _issued = [];

    internal SeedOperationSource(ITenantContext context)
    {
        _context = context;
        _resolved = context.IsResolved;
        _tenant = _resolved ? context.TenantId : null;
        _organization = _resolved ? context.OrganizationId : null;
        _actor = context.UserId;
    }

    public TrustedOperationDescriptor? Capture(Type requestType, ITenantContext context)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        ArgumentNullException.ThrowIfNull(context);
        if (!CanIssue || !ReferenceEquals(context, _context) || !Requests.Contains(requestType)
            || !IsEvidenceCurrent(_context)
            || (!_resolved && requestType != typeof(ProvisionTenantCommand))) return null;
        var descriptor = ForOperation(AuthorizationSourceKind.Seed, requestType, context, _context);
        _issued.Add(descriptor);
        return descriptor;
    }

    internal bool Owns(TrustedOperationDescriptor descriptor, Type requestType, ITenantContext context) =>
        _issued.Contains(descriptor) && ReferenceEquals(context, _context) && descriptor.Matches(requestType, context);

    protected override bool IsEvidenceCurrent(object? evidence) => ReferenceEquals(evidence, _context)
        && _context.IsResolved == _resolved && _context.HostScope is null
        && _context.UserId == _actor && (_actor is null || _actor == UserId.SystemActor)
        && (_resolved ? _context.Origin == TenantContextOrigin.Ambient
            && _context.TenantId == _tenant && _context.OrganizationId == _organization
            : _context.Origin is null);
}
