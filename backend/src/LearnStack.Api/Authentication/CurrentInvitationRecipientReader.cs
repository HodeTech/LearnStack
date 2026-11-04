using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using OpenTelemetry;

namespace LearnStack.Api.Authentication;

/// <summary>The fixed issuer UserInfo transport; no redirects, cookies, retry or certificate override.</summary>
internal sealed class CurrentInvitationRecipientBackchannel : IDisposable
{
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(5);
    internal const int MaximumResponseBytes = 16 * 1024;

    public CurrentInvitationRecipientBackchannel() : this(new SocketsHttpHandler()) { }

    // Controlled handlers exercise parsing/failure behavior without claiming real provider or TLS qualification.
    internal CurrentInvitationRecipientBackchannel(HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (handler is SocketsHttpHandler sockets)
        {
            if (sockets.SslOptions.RemoteCertificateValidationCallback is not null)
                throw new InvalidOperationException("Invitation recipient certificate validation cannot be replaced.");
            sockets.AllowAutoRedirect = false;
            sockets.UseCookies = false;
            sockets.ConnectTimeout = OperationTimeout;
        }
        Client = new HttpClient(handler) { Timeout = OperationTimeout, MaxResponseContentBufferSize = MaximumResponseBytes };
    }

    internal HttpClient Client { get; }
    public void Dispose() => Client.Dispose();
}

/// <summary>One uncached provider observation bound to the exact stock-validated request credential and invocation.</summary>
internal sealed class CurrentInvitationRecipientReader(IHttpContextAccessor accessor, TenantAuthenticationOptions settings,
    CurrentInvitationRecipientBackchannel backchannel)
    : CurrentInvitationRecipientEvidence.MintingSource, ICurrentInvitationRecipientReader
{
    private readonly HashSet<OperationInvocation> _observed = [];
    private readonly object _gate = new();

    public async Task<Result<CurrentInvitationRecipientEvidence>> ReadAsync(OperationInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        cancellationToken.ThrowIfCancellationRequested();
        var http = accessor.HttpContext;
        if (http is null || http.Features.Get<ValidatedInvitationCredential>() is not { } credential
            || !Matches(http, credential, invocation)) return Fail("lockey_dependency_unavailable");
        lock (_gate)
            if (!_observed.Add(invocation)) return Fail("lockey_dependency_unavailable");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, http.RequestAborted);
        deadline.CancelAfter(CurrentInvitationRecipientBackchannel.OperationTimeout);
        byte[]? bytes = null;
        try
        {
            using var suppression = SuppressInstrumentationScope.Begin();
            using var request = new HttpRequestMessage(HttpMethod.Get, settings.Issuer + "/protocol/openid-connect/userinfo");
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
            if (!credential.TryAuthorize(http, request)) return Fail("lockey_dependency_unavailable");
            using var response = await backchannel.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode
                || response.Content.Headers.ContentLength > CurrentInvitationRecipientBackchannel.MaximumResponseBytes)
                return Fail("lockey_dependency_unavailable");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            bytes = new byte[CurrentInvitationRecipientBackchannel.MaximumResponseBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token);
                if (read == 0) break;
                count += read;
            }
            deadline.Token.ThrowIfCancellationRequested();
            if (count > CurrentInvitationRecipientBackchannel.MaximumResponseBytes)
                return Fail("lockey_dependency_unavailable");
            if (!Matches(http, credential, invocation)) return Fail("lockey_dependency_unavailable");
            if (!TryClaims(bytes.AsMemory(0, count), credential.Subject.Subject, out var email))
                return Fail("lockey_forbidden");
            var observation = new Observation(http, credential, invocation, cancellationToken);
            var evidence = Capture(invocation, email!, observation);
            return evidence is null ? Fail("lockey_dependency_unavailable") : Result.Ok(evidence);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || http.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure) when (failure is HttpRequestException or IOException or OperationCanceledException)
        {
            // Neither provider messages nor credential-bearing request/response exceptions cross this boundary.
            return Fail("lockey_dependency_unavailable");
        }
        finally
        {
            if (bytes is not null) CryptographicOperations.ZeroMemory(bytes);
        }
    }

    protected override bool IsObservationCurrent(object observation, OperationInvocation invocation) =>
        observation is Observation captured && ReferenceEquals(captured.Invocation, invocation)
        && ReferenceEquals(accessor.HttpContext, captured.Http)
        && !captured.CancellationToken.IsCancellationRequested && Matches(captured.Http, captured.Credential, invocation);

    private bool Matches(HttpContext http, ValidatedInvitationCredential credential, OperationInvocation invocation) =>
        IsSourceOpen && credential.IsCurrent(http) && string.Equals(settings.Issuer, credential.Subject.Issuer, StringComparison.Ordinal)
        && InvitationEntryOperation.ForPath(http.Request.Path) is { } purpose && purpose.Matches(http)
        && purpose.RequestType == invocation.Policy.RequestType
        && http.Features.Get<InvitationEntrySource>() is { } entry && entry.MatchesInvocation(invocation)
        && invocation.IsActive && invocation.EntryKind == OperationEntryKind.Application
        && invocation.Policy.Kind == OperationPolicyKind.InvitationEntry
        && invocation.Context.Origin == TenantContextOrigin.HostAndInvitationOffer
        && invocation.Operation is InspectInvitationQuery or AcceptInvitationCommand
        && invocation.Operation.GetType() == invocation.Policy.RequestType
        && invocation.Candidate is { IsActive: true, SourceKind: AuthorizationSourceKind.Http }
        && http.Features.Get<TenantCredentialFeature>()?.Identity is { } identity
        && identity.ActorUserId == invocation.Actor
        && string.Equals(identity.Issuer, credential.Subject.Issuer, StringComparison.Ordinal)
        && string.Equals(identity.Subject, credential.Subject.Subject, StringComparison.Ordinal);

    private static bool TryClaims(ReadOnlyMemory<byte> bytes, string subject, out string? email)
    {
        email = null;
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return false;
            string? observedSubject = null;
            bool verified = false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name is not ("sub" or "email" or "email_verified")) continue;
                if (!seen.Add(property.Name)) return false;
                if (property.Name == "email_verified")
                {
                    if (property.Value.ValueKind != JsonValueKind.True) return false;
                    verified = true;
                }
                else
                {
                    if (property.Value.ValueKind != JsonValueKind.String) return false;
                    var value = property.Value.GetString();
                    if (property.Name == "sub") observedSubject = value;
                    else email = value;
                }
            }
            return seen.Count == 3 && verified && string.Equals(observedSubject, subject, StringComparison.Ordinal)
                && email is { Length: > 0 and <= 254 };
        }
        catch (JsonException) { return false; }
        catch (InvalidOperationException) { return false; }
    }

    private static Result<CurrentInvitationRecipientEvidence> Fail(string key) =>
        Result<CurrentInvitationRecipientEvidence>.Fail(new Error(LocalizedMessage.Of(key)));

    private sealed record Observation(HttpContext Http, ValidatedInvitationCredential Credential, OperationInvocation Invocation,
        CancellationToken CancellationToken)
    {
        public override string ToString() => nameof(Observation);
    }
}
