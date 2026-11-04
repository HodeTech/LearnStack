using LearnStack.Api.Account;
using LearnStack.Api.Identity;
using LearnStack.Api.Idempotency;
using LearnStack.Api.PublicReads;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Identity.Application.Contracts.Roles;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing.Matching;

namespace LearnStack.Api.Authentication;

/// <summary>Exact application classification shared with the live runtime guard.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal sealed class OperationPolicyAttribute(Type requestType) : Attribute
{
    public Type RequestType { get; } = requestType;
}

/// <summary>A named ASP.NET requirement consumes the same canonical application policy.</summary>
internal sealed class OperationPolicyRequirement(OperationPolicy policy) : IAuthorizationRequirement
{
    internal const string Prefix = "LearnStackOperation:";
    internal OperationPolicy Policy { get; } = policy;
    internal string Name => Prefix + Policy.Id;
}

internal enum InfrastructureOperationKind { Health = 1, OpenApi = 2, Documentation = 3 }

/// <summary>Only the three actual infrastructure mapping owners can attach these fixed declarations.</summary>
internal sealed class InfrastructureOperationMetadata
{
    private InfrastructureOperationMetadata(InfrastructureOperationKind kind) => Kind = kind;
    internal InfrastructureOperationKind Kind { get; }
    internal static InfrastructureOperationMetadata Health { get; } = new(InfrastructureOperationKind.Health);
    internal static InfrastructureOperationMetadata OpenApi { get; } = new(InfrastructureOperationKind.OpenApi);
    internal static InfrastructureOperationMetadata Documentation { get; } = new(InfrastructureOperationKind.Documentation);
}

internal enum HttpOperationClassification { Unmatched = 0, Refused = 1, Application = 2, Infrastructure = 3 }

/// <summary>Matched operations require an exact enumerated controller/action plus canonical metadata.</summary>
internal sealed class OperationHttpClassifier(IOperationPolicyCatalog catalog)
{
    internal HttpOperationClassification Classify(Endpoint? endpoint, out OperationPolicy? policy)
    {
        policy = null;
        if (endpoint is null || IsFrameworkMethodRejection(endpoint)) return HttpOperationClassification.Unmatched;
        var declarations = endpoint.Metadata.GetOrderedMetadata<OperationPolicyAttribute>();
        var infrastructure = endpoint.Metadata.GetOrderedMetadata<InfrastructureOperationMetadata>();
        if (infrastructure.Count != 0)
        {
            if (declarations.Count != 0 || infrastructure.Count != 1
                || endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not null)
                return HttpOperationClassification.Refused;
            return ReferenceEquals(infrastructure[0], InfrastructureOperationMetadata.Health)
                || ReferenceEquals(infrastructure[0], InfrastructureOperationMetadata.OpenApi)
                || ReferenceEquals(infrastructure[0], InfrastructureOperationMetadata.Documentation)
                ? HttpOperationClassification.Infrastructure : HttpOperationClassification.Refused;
        }
        if (declarations.Count != 1 || endpoint.Metadata.GetMetadata<ControllerActionDescriptor>() is not { } action
            || endpoint.Metadata.GetMetadata<IEndpointNameMetadata>() is not { } name
            || endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods
            || action.MethodInfo.DeclaringType != action.ControllerTypeInfo.AsType())
            return HttpOperationClassification.Refused;
        var expected = Expected(action.ControllerTypeInfo.AsType(), action.MethodInfo.Name);
        if (expected is null || declarations[0].RequestType != expected.Value.Request
            || name.EndpointName != expected.Value.Name || methods.HttpMethods.Count != 1
            || methods.HttpMethods[0] != expected.Value.Method || !catalog.TryGet(expected.Value.Request, out var found))
            return HttpOperationClassification.Refused;
        var authorization = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>();
        var anonymous = endpoint.Metadata.GetOrderedMetadata<IAllowAnonymous>();
        if (found.Kind == OperationPolicyKind.PublicRead)
        {
            if (anonymous.Count != 1 || authorization.Count != 0) return HttpOperationClassification.Refused;
        }
        else if (found.Kind is OperationPolicyKind.AccountRead or OperationPolicyKind.Protected
            or OperationPolicyKind.NativeSessionAdmission or OperationPolicyKind.OwnMembershipRead)
        {
            if (anonymous.Count != 0 || authorization.Count != 1
                || authorization[0].AuthenticationSchemes != TenantAuthenticationRegistration.Scheme
                || authorization[0].Policy != OperationPolicyRequirement.Prefix + found.Id
                || !string.IsNullOrEmpty(authorization[0].Roles)) return HttpOperationClassification.Refused;
        }
        else return HttpOperationClassification.Refused;
        var protectedOperation = ProtectedAdministrationDescriptor.ForRequest(found.RequestType);
        if (!HasCanonicalKeyMetadata(endpoint, action, protectedOperation))
            return HttpOperationClassification.Refused;
        policy = found;
        return HttpOperationClassification.Application;
    }

    private static bool HasCanonicalKeyMetadata(Endpoint endpoint, ControllerActionDescriptor action,
        ProtectedAdministrationDescriptor? operation)
    {
        var keyed = endpoint.Metadata.GetOrderedMetadata<IdempotentAttribute>();
        var declarations = action.EndpointMetadata?.OfType<IdempotentAttribute>().ToArray() ?? [];
        var filters = action.FilterDescriptors?.Where(filter => filter.Filter is IdempotentAttribute).ToArray() ?? [];
        if (operation is null) return keyed.Count == 0 && declarations.Length == 0 && filters.Length == 0;
        // MVC projects the same action filter through action metadata and the
        // filter descriptor. Two independent declarations are not this projection.
        return declarations.Length == 1 && filters.Length == 1 && filters[0].Scope == 30 && filters[0].Order == 0
            && ReferenceEquals(declarations[0], filters[0].Filter)
            && declarations[0].ProtectedOperation == operation.Operation
            && keyed.Count == 2 && keyed.All(value => ReferenceEquals(value, declarations[0]));
    }

    internal static bool RequiresProtectedAdmission(Endpoint? endpoint) =>
        endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
        && Expected(action.ControllerTypeInfo.AsType(), action.MethodInfo.Name) is { } expected
        && ProtectedAdministrationDescriptor.ForRequest(expected.Request) is not null;

    // ASP.NET's method matcher creates a non-route Endpoint with an empty metadata
    // collection and a private closure owned by this exact framework policy. Names
    // and lookalike application endpoints supply no such ownership proof.
    private static bool IsFrameworkMethodRejection(Endpoint endpoint) => endpoint.GetType() == typeof(Endpoint)
        && endpoint.Metadata.Count == 0
        && endpoint.RequestDelegate?.Method.DeclaringType?.DeclaringType == typeof(HttpMethodMatcherPolicy);

    private static (Type Request, string Name, string Method)? Expected(Type controller, string method) => (controller, method) switch
    {
        var pair when pair == (typeof(PublicSiteController), nameof(PublicSiteController.Get)) => (typeof(GetPublicSiteQuery), "GetPublicSite", "GET"),
        var pair when pair == (typeof(PublicSiteController), nameof(PublicSiteController.Head)) => (typeof(GetPublicSiteQuery), "HeadPublicSite", "HEAD"),
        var pair when pair == (typeof(PublicEducationController), nameof(PublicEducationController.Catalog)) => (typeof(GetPublicCoursesQuery), "GetPublicCourses", "GET"),
        var pair when pair == (typeof(PublicEducationController), nameof(PublicEducationController.HeadCatalog)) => (typeof(GetPublicCoursesQuery), "HeadPublicCourses", "HEAD"),
        var pair when pair == (typeof(PublicEducationController), nameof(PublicEducationController.Course)) => (typeof(GetPublicCourseQuery), "GetPublicCourse", "GET"),
        var pair when pair == (typeof(PublicEducationController), nameof(PublicEducationController.HeadCourse)) => (typeof(GetPublicCourseQuery), "HeadPublicCourse", "HEAD"),
        var pair when pair == (typeof(PublicEducationController), nameof(PublicEducationController.Lesson)) => (typeof(GetPublicLessonQuery), "GetPublicLesson", "GET"),
        var pair when pair == (typeof(PublicEducationController), nameof(PublicEducationController.HeadLesson)) => (typeof(GetPublicLessonQuery), "HeadPublicLesson", "HEAD"),
        var pair when pair == (typeof(AccountController), nameof(AccountController.GetSite)) => (typeof(GetAccountSiteQuery), "GetAccountSite", "GET"),
        var pair when pair == (typeof(MembershipsController), nameof(MembershipsController.List)) => (typeof(ListMembershipsQuery), "ListMemberships", "GET"),
        var pair when pair == (typeof(MembershipsController), nameof(MembershipsController.Get)) => (typeof(GetMembershipDetailQuery), "GetMembershipDetail", "GET"),
        var pair when pair == (typeof(MembershipsController), nameof(MembershipsController.SetProfile)) => (typeof(SetMembershipProfileCommand), "SetMembershipProfile", "PUT"),
        var pair when pair == (typeof(MembershipsController), nameof(MembershipsController.ReplaceRoles)) => (typeof(ReplaceMembershipRoleAssignmentsCommand), "ReplaceMembershipRoleAssignments", "PUT"),
        var pair when pair == (typeof(MembershipsController), nameof(MembershipsController.ChangeStatus)) => (typeof(ChangeMembershipStatusCommand), "ChangeMembershipStatus", "PUT"),
        var pair when pair == (typeof(OrganizationMembershipsController), nameof(OrganizationMembershipsController.List)) => (typeof(ListOrganizationMembershipsQuery), "ListOrganizationMemberships", "GET"),
        var pair when pair == (typeof(OwnMembershipController), nameof(OwnMembershipController.GetProfile)) => (typeof(GetOwnMembershipProfileQuery), "GetOwnMembershipProfile", "GET"),
        var pair when pair == (typeof(OwnMembershipController), nameof(OwnMembershipController.SetProfile)) => (typeof(SetOwnMembershipProfileCommand), "SetOwnMembershipProfile", "PUT"),
        var pair when pair == (typeof(OwnMembershipController), nameof(OwnMembershipController.Contexts)) => (typeof(GetOwnMembershipContextsQuery), "ListOwnMembershipContexts", "GET"),
        var pair when pair == (typeof(OwnMembershipController), nameof(OwnMembershipController.Capabilities)) => (typeof(GetOwnCapabilitiesQuery), "GetOwnCapabilities", "GET"),
        var pair when pair == (typeof(RolesController), nameof(RolesController.List)) => (typeof(ListRolesQuery), "ListRoles", "GET"),
        var pair when pair == (typeof(RolesController), nameof(RolesController.Get)) => (typeof(GetRoleDetailQuery), "GetRoleDetail", "GET"),
        var pair when pair == (typeof(RolesController), nameof(RolesController.ChangeGrants)) => (typeof(ChangeRoleGrantsCommand), "ChangeRoleGrants", "PUT"),
        var pair when pair == (typeof(RolesController), nameof(RolesController.AdoptManifest)) => (typeof(AdoptRoleGrantManifestCommand), "AdoptRoleGrantManifest", "POST"),
        var pair when pair == (typeof(PermissionsController), nameof(PermissionsController.List)) => (typeof(GetPermissionRegistryQuery), "GetPermissionRegistry", "GET"),
        var pair when pair == (typeof(OrganizationsController), nameof(OrganizationsController.List)) => (typeof(ListOrganizationsQuery), "ListOrganizations", "GET"),
        var pair when pair == (typeof(OrganizationsController), nameof(OrganizationsController.Get)) => (typeof(GetOrganizationDetailQuery), "GetOrganizationDetail", "GET"),
        var pair when pair == (typeof(OrganizationsController), nameof(OrganizationsController.Create)) => (typeof(CreateManagedOrganizationCommand), "CreateManagedOrganization", "POST"),
        var pair when pair == (typeof(OrganizationsController), nameof(OrganizationsController.Rename)) => (typeof(RenameOrganizationCommand), "RenameOrganization", "PUT"),
        var pair when pair == (typeof(OrganizationsController), nameof(OrganizationsController.Archive)) => (typeof(ArchiveOrganizationCommand), "ArchiveOrganization", "POST"),
        var pair when pair == (typeof(DefaultOrganizationController), nameof(DefaultOrganizationController.Select)) => (typeof(SelectDefaultOrganizationCommand), "SelectDefaultOrganization", "PUT"),
        var pair when pair == (typeof(NativeSessionAdmissionController), nameof(NativeSessionAdmissionController.Admit)) =>
            (typeof(AdmitNativeSessionScopeCommand), "AdmitNativeSessionScope", "POST"),
        _ => null,
    };
}
