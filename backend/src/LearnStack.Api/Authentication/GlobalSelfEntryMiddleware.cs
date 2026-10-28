using System.Globalization;
using System.Text;
using System.Text.Json;
using LearnStack.Api.Common;
using LearnStack.Api.PublicReads;
using LearnStack.Api.Tenancy;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Errors;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;

namespace LearnStack.Api.Authentication;

/// <summary>The exact global-self exception terminates before ordinary binding, resolution or MediatR.</summary>
internal sealed class GlobalSelfEntryMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, HttpIdentitySelfEntryAdmission admission,
        Func<IIdentitySelfStateReader> states, Func<IIdentitySelfBootstrapper> bootstrap, Func<IIdentitySelfDiscovery> discovery,
        Func<IOwnMembershipScopeLabels> labels, NativeEntryOriginOptions origins)
    {
        var operation = GlobalSelfOperation.ForPath(http.Request.Path);
        if (operation is null) { await next(http); return; }
        PublicResponsePolicy.Apply(http.Response);
        http.Response.OnStarting(() => { PublicResponsePolicy.Apply(http.Response); return Task.CompletedTask; });
        if (http.Features.Get<NativeSessionSourceFeature>() is not { GlobalOperation: { } captured } source
            || !ReferenceEquals(captured, operation) || !source.IsCurrent(http))
        { await ProblemAsync(http, 404); return; }
        if (!string.Equals(http.Request.Method, operation.Method, StringComparison.Ordinal))
        { http.Response.Headers.Allow = operation.Method; await ProblemAsync(http, 405); return; }
        if (http.Request.Headers.ContainsKey("Idempotency-Key")
            || TenantAssertionMiddleware.HasAssertionHeaders(http.Request.Headers))
        { await ProblemAsync(http, 400); return; }
        var entry = admission.CurrentSubject(http, operation.Kind);
        if (entry is null)
        { await ProblemAsync(http, source.IsCurrent(http) ? 503 : 401); return; }
        var subject = entry.Subject;
        if (operation.Kind == IdentitySelfOperation.Contexts)
        {
            await ContextsAsync(http, source, entry, discovery, labels, origins);
            return;
        }
        if (http.Request.Query.Count != 0
            || (operation.Kind == IdentitySelfOperation.Bootstrap
                ? !await EmptyNativeJsonBody.ReadAsync(http)
                : HasBody(http)))
        { await ProblemAsync(http, 400); return; }
        IdentityBindingResult binding;
        var status = 200;
        if (operation.Kind == IdentitySelfOperation.Bootstrap)
        {
            var result = await bootstrap().BootstrapAsync(subject, entry.ClaimedActor, http.RequestAborted);
            if (!source.IsCurrent(http)) { await ProblemAsync(http, 401); return; }
            if (result.Refusal is { } refusal)
            {
                if (result.RetryAfterSeconds is { } seconds)
                    http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
                await ProblemAsync(http, refusal switch
                { IdentityCreationRefusal.Disabled => 403, IdentityCreationRefusal.WindowExhausted => 429, _ => 503 });
                return;
            }
            binding = result.Binding;
            if (binding.Outcome == IdentityBindingOutcome.Created)
            { status = 201; http.Response.Headers.Location = "/api/v1/identity/self"; }
        }
        else binding = await states().ReadSelfAsync(subject, entry.ClaimedActor, http.RequestAborted);
        if (!source.IsCurrent(http)) { await ProblemAsync(http, 401); return; }
        if (binding.Outcome == IdentityBindingOutcome.NotFound && operation.Kind == IdentitySelfOperation.State)
        { await JsonAsync(http, new { state = "missing" }, 200); return; }
        if (!binding.IsBound) { await ProblemAsync(http, BindingStatus(binding)); return; }
        await JsonAsync(http, new
        {
            state = "active",
            authorityId = subject.AuthorityId.Value.ToString("D"),
            subjectKey = subject.SubjectKey.Hex,
            actorId = binding.Actor!.Value.Value.ToString("D"),
        }, status);
    }

    private static async Task ContextsAsync(HttpContext http, NativeSessionSourceFeature source,
        NativeEntrySubject entry, Func<IIdentitySelfDiscovery> discovery,
        Func<IOwnMembershipScopeLabels> labels, NativeEntryOriginOptions origins)
    {
        var subject = entry.Subject;
        if (HasBody(http))
        { await ProblemAsync(http, 400); return; }
        if (!TryPage(http, subject, out var after, out var limit, out var invalidField))
        { await ProblemAsync(http, 400, invalidField); return; }
        using var page = await discovery().ReadScopesAsync(subject, entry.ClaimedActor, after, limit, http.RequestAborted);
        if (!page.Binding.IsBound) { await ProblemAsync(http, BindingStatus(page.Binding)); return; }
        var items = new List<SelfContextItem>();
        var host = http.Features.Get<HostClassification>()!;
        foreach (var scope in page.Scopes)
        {
            if (!source.IsCurrent(http)) { await ProblemAsync(http, 401); return; }
            OwnMembershipScopeLabel? label;
            try { label = await labels().ReadAsync(scope, http.RequestAborted); }
            catch (InfrastructureException) when (!source.IsCurrent(http))
            { await ProblemAsync(http, 401); return; }
            if (!source.IsCurrent(http) || !scope.IsActive) { await ProblemAsync(http, 401); return; }
            if (label is not null) items.Add(new(scope.TenantId.Value.ToString("D"),
                scope.OrganizationId?.Value.ToString("D"), label.TenantName, label.OrganizationName,
                origins.ForHost(label.EntryHost), host.TenantId == scope.TenantId
                    && (host.OrganizationId is null || host.OrganizationId == scope.OrganizationId)));
        }
        var hasNext = page.HasNext;
        var cursor = hasNext ? EncodeCursor(subject, page.Scopes[^1].MembershipId) : null;
        if (!source.IsCurrent(http)) { await ProblemAsync(http, 401); return; }
        await JsonAsync(http, new Page<SelfContextItem>(items, new PageInfo(cursor, null, hasNext, false)), 200);
    }

    private static bool TryPage(HttpContext http, GlobalIdentitySubject subject, out Guid? after, out int limit,
        out string? invalidField)
    {
        after = null;
        limit = 20;
        invalidField = null;
        foreach (var pair in http.Request.Query)
        {
            if (pair.Key is not ("limit" or "cursor")) return false;
            if (pair.Value.Count != 1 || string.IsNullOrEmpty(pair.Value[0]))
            { invalidField = pair.Key; return false; }
        }
        if (http.Request.Query.TryGetValue("limit", out var stated))
        {
            if (!int.TryParse(stated[0], NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed < 1
                || parsed.ToString(CultureInfo.InvariantCulture) != stated[0])
            { invalidField = "limit"; return false; }
            limit = Math.Min(parsed, 100);
        }
        if (!http.Request.Query.TryGetValue("cursor", out var cursor)) return true;
        invalidField = "cursor";
        if (cursor[0] is not { Length: > 0 and <= 1024 } value) return false;
        try
        {
            var bytes = WebEncoders.Base64UrlDecode(value);
            if (WebEncoders.Base64UrlEncode(bytes) != value) return false;
            var parts = Encoding.UTF8.GetString(bytes).Split(':');
            if (parts.Length != 3 || parts[0] != subject.AuthorityId.Value.ToString("D")
                || parts[1] != subject.SubjectKey.Hex || !Guid.TryParseExact(parts[2], "D", out var id)
                || id == Guid.Empty || id.ToString("D") != parts[2]) return false;
            after = id;
            invalidField = null;
            return true;
        }
        catch (FormatException) { return false; }
    }

    private static string EncodeCursor(GlobalIdentitySubject subject, Guid after) => WebEncoders.Base64UrlEncode(
        Encoding.UTF8.GetBytes(subject.AuthorityId.Value.ToString("D") + ":" + subject.SubjectKey.Hex + ":" + after.ToString("D")));

    private static bool HasBody(HttpContext http) => http.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody is true
        || http.Request.ContentLength is > 0
        || http.Request.Headers.ContainsKey("Transfer-Encoding");

    private static int BindingStatus(IdentityBindingResult result) => result.Outcome is
        IdentityBindingOutcome.AuthorityUnavailable or IdentityBindingOutcome.StorageUnavailable or IdentityBindingOutcome.Indeterminate
        ? 503 : 401;

    private static Task ProblemAsync(HttpContext http, int status, string? field = null)
    {
        http.Response.StatusCode = status;
        if (status != 429) http.Response.Headers.Remove("Retry-After");
        if (status == 401) http.Response.Headers.WWWAuthenticate = "Bearer";
        var problem = status == 400
            ? ProblemDetailsFactory.For(new Error(new LocalizedMessage("lockey_validation_failed"), field is null ? null
                : new Dictionary<string, IReadOnlyList<LocalizedMessage>>
                { [field] = [new LocalizedMessage(ModelBindingProblemDetails.FieldMessageKey)] }), http)
            : ProblemDetailsFactory.ForStatus(status, http);
        return http.Response.WriteAsJsonAsync(problem, options: null,
            contentType: ProblemDetailsMediaType.Value, cancellationToken: http.RequestAborted);
    }

    private static async Task JsonAsync(HttpContext http, object value, int status)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonSerializerOptions.Web);
        if (bytes.Length > 256 * 1024) { await ProblemAsync(http, 503); return; }
        http.Response.StatusCode = status;
        http.Response.ContentType = "application/json";
        await http.Response.Body.WriteAsync(bytes, http.RequestAborted);
    }

    private sealed record SelfContextItem(string TenantId, string? OrganizationId, string TenantName,
        string? OrganizationName, string EntryOrigin, bool CanUseCurrentHost);
}

internal sealed class NativeEntryOriginOptions
{
    private readonly int _port;
    internal NativeEntryOriginOptions(IConfiguration configuration)
    {
        var value = configuration["Identity:NativeSession:HttpsPort"];
        _port = 443;
        if (value is not null && (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _port)
            || _port is < 1 or > 65535 || _port.ToString(CultureInfo.InvariantCulture) != value))
            throw new InvalidOperationException("Native entry origin configuration is invalid.");
    }
    internal string ForHost(string normalizedHost) => new UriBuilder("https", normalizedHost, _port).Uri.GetLeftPart(UriPartial.Authority);
}
