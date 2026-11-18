using LearnStack.Api.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;

namespace LearnStack.Api.Authentication;

/// <summary>
/// ADR-0099 Amendment 6 (S-B): the test record is invocable only with a token carrying the optional
/// <c>federation.test_record</c> client scope, which only the BFF's <c>federation_test</c> authorization requests.
/// An ordinary sign-in or Studio token never carries it.
/// </summary>
internal static class FederationTestRecordScope
{
    internal const string Name = "federation.test_record";

    /// <summary>Whether this request's verified credential carries the scope.</summary>
    internal static bool IsPresent(HttpContext http) =>
        http.Features.Get<TenantCredentialFeature>() is { VerifiedSubject.FederationTestRecord: true };

    /// <summary>The closed 403 the test record answers without the scope, and every other endpoint answers with it.</summary>
    internal static Error Refusal { get; } = new(LocalizedMessage.Of("lockey_forbidden"));

    /// <summary>
    /// ADR-0099 Amendment 8: whether the matched endpoint is the test record itself, by its action identity, never by
    /// its path. It is the only endpoint a token carrying the scope reaches.
    /// </summary>
    internal static bool IsTestRecordEndpoint(HttpContext http) =>
        http.GetEndpoint()?.Metadata.GetMetadata<ControllerActionDescriptor>() is { } action
        && action.ControllerTypeInfo.AsType() == typeof(FederationController)
        && action.MethodInfo.Name == nameof(FederationController.RecordTestResult);
}
