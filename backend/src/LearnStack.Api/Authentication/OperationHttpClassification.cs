using LearnStack.Api.Account;
using LearnStack.Api.PublicReads;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Permissions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LearnStack.Api.Authentication;

/// <summary>Exact application classification; runtime enforcement is activated with trusted evidence.</summary>
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
        if (endpoint is null) return HttpOperationClassification.Unmatched;
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
            || endpoint.Metadata.GetMetadata<IHttpMethodMetadata>() is not { } methods)
            return HttpOperationClassification.Refused;
        var expected = Expected(action.ControllerTypeInfo.AsType(), action.MethodInfo.Name);
        if (expected is null || declarations[0].RequestType != expected.Value.Request
            || name.EndpointName != expected.Value.Name || methods.HttpMethods.Count != 1
            || methods.HttpMethods[0] != expected.Value.Method || !catalog.TryGet(expected.Value.Request, out var found))
            return HttpOperationClassification.Refused;
        policy = found;
        return HttpOperationClassification.Application;
    }

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
        _ => null,
    };
}
