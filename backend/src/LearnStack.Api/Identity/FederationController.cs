using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using LearnStack.Api.Authentication;
using LearnStack.Api.Common;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Identity;

/// <summary>
/// ADR-0099 tenant federation administration. Thin: every action parses its closed body, reads <c>If-Match</c>
/// and hands the operation to <see cref="FederationOperationRunner"/>. No <c>Idempotency-Key</c>: the mutations
/// converge through the connection revision, the pending operation and a fresh read. No DELETE: the native
/// transport admits none, so deletion is a POST to an action resource.
/// </summary>
[Route("identity/federation")]
public sealed class FederationController(FederationOperationRunner runner) : ApiControllerBase
{
    private static readonly string[] ConfigureFields =
    [
        "protocol", "displayName", "source", "documentUrl", "issuerOrEntityId", "authorizationEndpoint", "tokenEndpoint",
        "jwksEndpoint", "userInfoEndpoint", "singleSignOnEndpoint", "signingCertificates", "clientId", "clientAuthentication", "clientSecret",
    ];

    [HttpGet(Name = "GetFederationConnection")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme,
        Policy = OperationPolicyRequirement.Prefix + IdentityOperationPolicySource.FederationReadOperation)]
    [OperationPolicy(typeof(GetFederationConnectionQuery))]
    [ProducesResponseType<FederationConnectionResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (Request.Query.Count != 0 || !await AdministrationHttpInput.ReadHasNoBodyAsync(HttpContext)) return AdministrationHttpInput.Invalid();
        var result = await runner.ReadAsync(cancellationToken);
        if (result.IsFailure) return result.ToActionResult();
        if (result.Value!.Connection is { } connection) Response.Headers.ETag = EntityTag.For(connection.Version);
        return Ok(FederationHttpModels.Detail(result.Value));
    }

    [HttpPut("configuration", Name = "ConfigureFederation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.configure")]
    [OperationPolicy(typeof(BeginConfigureFederationCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Configure(CancellationToken cancellationToken)
    {
        using var body = await BodyAsync(ConfigureFields);
        // If-Match is absent only for the first configuration of a tenant with no live connection.
        long? expected = null;
        if (Request.Headers.IfMatch.Count != 0)
        {
            if (!ProtectedEntityTag.TryRead(Request, out var version)) return AdministrationHttpInput.Invalid();
            expected = version;
        }
        if (body is null || !FederationHttpModels.TryConfigure(body.RootElement, out var input)) return AdministrationHttpInput.Invalid();
        return FederationHttpModels.Receipt(await runner.ConfigureAsync(input!, expected, cancellationToken));
    }

    [HttpPost("secret-rotations", Name = "RotateFederationSecret")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.rotate_secret")]
    [OperationPolicy(typeof(BeginRotateFederationSecretCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RotateSecret(CancellationToken cancellationToken)
    {
        using var body = await BodyAsync("clientSecret");
        if (body is null || !ProtectedEntityTag.TryRead(Request, out var version)
            || body.RootElement.GetProperty("clientSecret") is not { ValueKind: JsonValueKind.String } raw
            || !FederationClientSecret.TryCreate(raw.GetString(), out var secret)) return AdministrationHttpInput.Invalid();
        return FederationHttpModels.Receipt(await runner.RotateSecretAsync(version, secret!, cancellationToken));
    }

    [HttpPut("display-name", Name = "RenameFederation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.rename")]
    [OperationPolicy(typeof(BeginRenameFederationCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Rename(CancellationToken cancellationToken)
    {
        using var body = await BodyAsync("displayName");
        if (body is null || !ProtectedEntityTag.TryRead(Request, out var version)
            || !AdministrationHttpInput.TryText(body.RootElement, "displayName", false, 320, out var name)) return AdministrationHttpInput.Invalid();
        return FederationHttpModels.Receipt(await runner.RenameAsync(version, name!, cancellationToken));
    }

    [HttpPost("test-starts", Name = "StartFederationTest")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.test_start")]
    [OperationPolicy(typeof(BeginFederationTestStartCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> StartTest(CancellationToken cancellationToken) =>
        await EmptyAsync() is { } version ? FederationHttpModels.Receipt(await runner.StartTestAsync(version, cancellationToken)) : AdministrationHttpInput.Invalid();

    /// <summary>
    /// The BFF's server-side test callback reports the attempt it holds, with the identifier kept from the browser. ADR-0099
    /// Amendment 6 (S-B): the token must also carry the optional <c>federation.test_record</c> client scope, which only the
    /// <c>federation_test</c> authorization requests; without it the closed 403 refuses before the body is read.
    /// </summary>
    [HttpPost("test-results", Name = "RecordFederationTestResult")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.test_record")]
    [OperationPolicy(typeof(RecordFederationTestResultCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RecordTestResult(CancellationToken cancellationToken)
    {
        if (!FederationTestRecordScope.IsPresent(HttpContext))
            return Result<FederationRun>.Fail(new Error(LocalizedMessage.Of("lockey_forbidden"))).ToActionResult();
        using var body = await BodyAsync("testAttemptId", "passed");
        if (body is null || !ProtectedEntityTag.TryRead(Request, out var version)
            || !AdministrationHttpInput.TryText(body.RootElement, "testAttemptId", false, 36, out var text)
            || !AdministrationHttpInput.TryId(text, out var attempt)
            || body.RootElement.GetProperty("passed").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return AdministrationHttpInput.Invalid();
        return FederationHttpModels.Receipt(await runner.RecordTestResultAsync(version, attempt,
            body.RootElement.GetProperty("passed").GetBoolean(), cancellationToken));
    }

    [HttpPost("activation", Name = "ActivateFederation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.activate")]
    [OperationPolicy(typeof(ActivateFederationCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Activate(CancellationToken cancellationToken) =>
        await EmptyAsync() is { } version ? FederationHttpModels.Receipt(await runner.ActivateAsync(version, cancellationToken)) : AdministrationHttpInput.Invalid();

    [HttpPost("disablement", Name = "DisableFederation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.disable")]
    [OperationPolicy(typeof(BeginDisableFederationCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Disable(CancellationToken cancellationToken) =>
        await EmptyAsync() is { } version ? FederationHttpModels.Receipt(await runner.DisableAsync(version, cancellationToken)) : AdministrationHttpInput.Invalid();

    [HttpPost("enablement", Name = "EnableFederation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.enable")]
    [OperationPolicy(typeof(EnableFederationCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Enable(CancellationToken cancellationToken) =>
        await EmptyAsync() is { } version ? FederationHttpModels.Receipt(await runner.EnableAsync(version, cancellationToken)) : AdministrationHttpInput.Invalid();

    [HttpPost("deletion", Name = "DeleteFederation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.delete")]
    [OperationPolicy(typeof(BeginDeleteFederationCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Delete(CancellationToken cancellationToken) =>
        await EmptyAsync() is { } version ? FederationHttpModels.Receipt(await runner.DeleteAsync(version, cancellationToken)) : AdministrationHttpInput.Invalid();

    [HttpPost("resumption", Name = "ResumeFederationOperation")]
    [Authorize(AuthenticationSchemes = TenantAuthenticationRegistration.Scheme, Policy = OperationPolicyRequirement.Prefix + "identity.federation.resume")]
    [OperationPolicy(typeof(ResumeFederationOperationCommand))]
    [ProducesResponseType<FederationOperationResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Resume(CancellationToken cancellationToken) =>
        await EmptyAsync() is { } version ? FederationHttpModels.Receipt(await runner.ResumeAsync(version, cancellationToken)) : AdministrationHttpInput.Invalid();

    /// <summary>An action resource takes an empty JSON object and the connection revision in <c>If-Match</c>.</summary>
    private async Task<long?> EmptyAsync()
    {
        using var body = await BodyAsync();
        return body is not null && ProtectedEntityTag.TryRead(Request, out var version) ? version : null;
    }

    /// <summary>ADR-0099: these mutations accept no <c>Idempotency-Key</c>; a supplied one is refused, never ignored.</summary>
    private Task<JsonDocument?> BodyAsync(params string[] fields) => Request.Headers.ContainsKey("Idempotency-Key")
        ? Task.FromResult<JsonDocument?>(null) : AdministrationHttpInput.ReadObjectAsync(HttpContext, fields);
}

public sealed record FederationPendingResponse(string Kind, DateTimeOffset StartedAt, DateTimeOffset QuietUntil);

/// <summary>Nonsecret connection detail; no actor, test attempt, secret or certificate.</summary>
public sealed record FederationConnectionBody(string ConnectionId, string Protocol, string Alias, string DisplayName, string IssuerOrEntityId,
    string Status, string Source, string? ClientId, string ConfigurationRevision, string ConfigurationDigest, string DesiredDigest,
    string? LastProviderDigest, FederationPendingResponse? Pending, DateTimeOffset? TestExpiresAt, string Version);

public sealed record FederationConnectionResponse(FederationConnectionBody? Connection, string Provider);

/// <summary>Every settled outcome is 200; <c>testAttemptId</c> is set only for an applied test start, for the BFF's server-side owner.</summary>
public sealed record FederationOperationResponse(string ConnectionId, string Version, string Outcome, string? Reason, string? TestAttemptId);

internal static class FederationHttpModels
{
    internal static IActionResult Receipt(Result<FederationRun> result)
    {
        if (result.IsFailure) return result.ToActionResult();
        var run = result.Value!;
        return new OkObjectResult(new FederationOperationResponse(run.Receipt.ConnectionId.ToString("D"),
            AdministrationHttpInput.Version(Math.Max(run.Receipt.Version, 0)), Snake(run.Receipt.Outcome.ToString()),
            run.Receipt.Reason is { } reason ? Snake(reason.ToString()) : null, run.TestAttemptId?.ToString("D")));
    }

    internal static FederationConnectionResponse Detail(FederationConnectionDetail detail) => new(detail.Connection is { } view
        ? new FederationConnectionBody(view.ConnectionId.ToString("D"), Snake(view.Protocol.ToString()), view.Alias, view.DisplayName,
            view.IssuerOrEntityId, Snake(view.Status.ToString()), Snake(view.Source.ToString()), view.ClientId,
            AdministrationHttpInput.Version(view.ConfigurationRevision), view.ConfigurationDigest, view.DesiredDigest, view.LastProviderDigest,
            view.Pending is { } pending ? new FederationPendingResponse(Snake(pending.Kind.ToString()), pending.StartedAt, pending.QuietUntil) : null,
            view.TestExpiresAt, AdministrationHttpInput.Version(view.Version))
        : null, Snake(detail.Provider.ToString()));

    internal static bool TryConfigure(JsonElement body, out FederationConfigureInput? input)
    {
        input = null;
        if (!TryEnum<FederationBrokerProtocol>(body, "protocol", false, out var protocol) || protocol is null
            || !TryEnum<FederationSource>(body, "source", false, out var source) || source is null
            || !TryEnum<FederationClientAuthentication>(body, "clientAuthentication", true, out var authentication)
            || !AdministrationHttpInput.TryText(body, "displayName", true, 320, out var displayName)
            || !TryUri(body, "documentUrl", out var document) || !AdministrationHttpInput.TryText(body, "issuerOrEntityId", true, 2048, out var issuer)
            || !TryUri(body, "authorizationEndpoint", out var authorization) || !TryUri(body, "tokenEndpoint", out var token)
            || !TryUri(body, "jwksEndpoint", out var jwks) || !TryUri(body, "userInfoEndpoint", out var userInfo)
            || !TryUri(body, "singleSignOnEndpoint", out var singleSignOn)
            || !AdministrationHttpInput.TryText(body, "clientId", true, 255, out var clientId)
            || !TryCertificates(body, out var certificates))
            return false;
        FederationClientSecret? secret = null;
        var raw = body.GetProperty("clientSecret");
        if (raw.ValueKind != JsonValueKind.Null && (raw.ValueKind != JsonValueKind.String || !FederationClientSecret.TryCreate(raw.GetString(), out secret)))
            return false;
        if (protocol == FederationBrokerProtocol.Oidc != authentication is not null) return false;
        input = new FederationConfigureInput(protocol.Value, displayName, source.Value, document, issuer, authorization, token, jwks, userInfo,
            singleSignOn, certificates, clientId, authentication ?? FederationClientAuthentication.ClientSecretBasic, secret);
        return true;
    }

    private static bool TryEnum<TEnum>(JsonElement body, string name, bool nullable, out TEnum? value) where TEnum : struct, Enum
    {
        value = null;
        if (!AdministrationHttpInput.TryText(body, name, nullable, 32, out var text)) return false;
        if (text is null) return nullable;
        foreach (var candidate in Enum.GetValues<TEnum>())
            if (string.Equals(Snake(candidate.ToString()), text, StringComparison.Ordinal)) { value = candidate; return true; }
        return false;
    }

    private static bool TryUri(JsonElement body, string name, out Uri? value)
    {
        value = null;
        if (!AdministrationHttpInput.TryText(body, name, true, 2048, out var text)) return false;
        if (text is null) return true;
        return Uri.TryCreate(text, UriKind.Absolute, out value) && value.Scheme == Uri.UriSchemeHttps;
    }

    private static bool TryCertificates(JsonElement body, out ImmutableArray<string> certificates)
    {
        certificates = [];
        var value = body.GetProperty("signingCertificates");
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > FederationCertificates.MaximumCount) return false;
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= FederationCertificates.MaximumPemLength } pem)
                return false;
            builder.Add(pem);
        }
        certificates = builder.ToImmutable();
        return true;
    }

    /// <summary>PascalCase enum names become the closed snake_case wire values.</summary>
    internal static string Snake(string value) => string.Concat(value.Select((character, index) =>
        char.IsUpper(character) ? (index == 0 ? string.Empty : "_") + char.ToLower(character, CultureInfo.InvariantCulture) : character.ToString()));
}
