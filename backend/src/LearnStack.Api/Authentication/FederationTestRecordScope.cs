using Microsoft.AspNetCore.Http;

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
}
