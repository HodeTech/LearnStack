using LearnStack.Api.Tenancy;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Identity;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LearnStack.Api.Authentication;

/// <summary>Closed HTTP purposes for retained own-consent entry.</summary>
internal sealed class ConsentSubjectOperation(string path, string method, string action, Type requestType, ConsentSubjectPurpose purpose)
{
    internal Type RequestType { get; } = requestType;
    internal ConsentSubjectPurpose Purpose { get; } = purpose;
    private static readonly ConsentSubjectOperation Scopes = new("/api/v1/tenancy/membership-field-consents/own/scopes", "GET", nameof(ConsentSubjectController.Scopes), typeof(ListOwnFieldConsentScopesQuery), ConsentSubjectPurpose.Discover);
    private static readonly ConsentSubjectOperation History = new("/api/v1/tenancy/membership-field-consents/own/history", "GET", nameof(ConsentSubjectController.History), typeof(ListOwnMembershipFieldConsentsQuery), ConsentSubjectPurpose.History);
    private static readonly ConsentSubjectOperation Withdraw = new("/api/v1/tenancy/membership-field-consents/withdrawals", "POST", nameof(ConsentSubjectController.Withdraw), typeof(WithdrawOwnMembershipFieldConsentCommand), ConsentSubjectPurpose.Withdraw);
    internal static ConsentSubjectOperation? ForPath(PathString path) => path.Value switch
    {
        "/api/v1/tenancy/membership-field-consents/own/scopes" => Scopes,
        "/api/v1/tenancy/membership-field-consents/own/history" => History,
        "/api/v1/tenancy/membership-field-consents/withdrawals" => Withdraw,
        _ => null
    };
    internal bool Matches(HttpContext http) => http.Request.Path == path && http.Request.Method == method
        && ReferenceEquals(ForPath(http.Request.Path), this)
        && http.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } descriptor
        && descriptor.ControllerTypeInfo.Assembly == typeof(ConsentSubjectOperation).Assembly
        && descriptor.ControllerTypeInfo.FullName == "LearnStack.Api.Tenancy.ConsentSubjectController"
        && descriptor.MethodInfo.DeclaringType == descriptor.ControllerTypeInfo.AsType() && descriptor.MethodInfo.Name == action
        && http.GetEndpoint()?.Metadata.GetMetadata<OperationPolicyAttribute>()?.RequestType == RequestType;
}
