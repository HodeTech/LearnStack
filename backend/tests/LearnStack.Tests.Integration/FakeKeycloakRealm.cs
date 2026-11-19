using System.Net;
using System.Text.Json;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Tools.IdentityAuthorityRebind;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LearnStack.Tests.Integration;

/// <summary>A loopback realm that answers like Keycloak's discovery, key set, token and administration endpoints.</summary>
internal sealed class FakeKeycloakRealm : IAsyncDisposable
{
    internal const string ClientId = "rebind-reader";
    internal const string ClientSecret = "reader-secret-value";

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private string? _failure;
    private int _pages;

    private FakeKeycloakRealm(WebApplication app, List<string> users)
    {
        _app = app;
        Users = users;
        _client = KeycloakRealmContinuitySource.CreateClient();
    }

    internal List<string> Users { get; }

    internal string Issuer { get; private set; } = "";

    internal string? DiscoveryIssuer { get; set; }

    /// <summary>
    /// A reachable origin other than the issuer's: when set, the discovery document names its key set and token endpoint
    /// there, so a reader that followed them would send the credential to a host that is up.
    /// </summary>
    internal string? ForeignOrigin { get; set; }

    internal int? ReportedCount { get; set; }

    internal int? CountAfterRead { get; set; }

    internal int Pages => Volatile.Read(ref _pages);

    internal List<Dictionary<string, string>> TokenRequests { get; } = [];

    internal List<string?> AdminAuthorization { get; } = [];

    internal static Task<FakeKeycloakRealm> StartAsync(int users) =>
        StartAsync(Enumerable.Range(0, users).Select(_ => Guid.NewGuid().ToString("D")));

    internal static async Task<FakeKeycloakRealm> StartAsync(IEnumerable<string> users)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var realm = new FakeKeycloakRealm(app, users.ToList());
        realm.Map(app);
        await app.StartAsync();
        var origin = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        realm.Issuer = origin + "/realms/learnstack";
        return realm;
    }

    internal void Fail(string failure) => _failure = failure;

    internal Task<NewRealmSnapshot> ReadAsync() =>
        new KeycloakRealmContinuitySource(_client, ClientId, ClientSecret).ReadAsync(Issuer);

    private void Map(WebApplication app)
    {
        app.MapGet("/realms/learnstack/.well-known/openid-configuration", context =>
        {
            if (_failure == "discovery-redirect")
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = "https://elsewhere.example.test/";
                return Task.CompletedTask;
            }

            var foreign = ForeignOrigin ?? "http://127.0.0.1:1";
            var jwks = _failure == "jwks-other-origin" ? foreign + "/realms/learnstack/keys" : Issuer + "/keys";
            var token = _failure == "token-other-origin"
                ? foreign + "/realms/learnstack/protocol/openid-connect/token"
                : Issuer + "/protocol/openid-connect/token";
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                issuer = DiscoveryIssuer ?? Issuer,
                jwks_uri = jwks,
                token_endpoint = token,
            }));
        });
        app.MapGet("/realms/learnstack/keys", context =>
        {
            context.Response.ContentType = "application/json";
            return context.Response.WriteAsync(
                """{"keys":[{"kty":"RSA","kid":"a","use":"sig"},{"kty":"RSA","kid":"b"},{"kty":"RSA","kid":"c","use":"enc"}]}""");
        });
        app.MapPost("/realms/learnstack/protocol/openid-connect/token", async context =>
        {
            var form = await context.Request.ReadFormAsync();
            TokenRequests.Add(form.ToDictionary(pair => pair.Key, pair => pair.Value.ToString()));
            if (_failure == "token-denied")
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(_failure == "oversized-token-answer"
                ? "{\"access_token\":\"" + new string('x', 300 * 1024) + "\"}"
                : """{"access_token":"realm-token"}""");
        });
        app.MapGet("/admin/realms/learnstack/users/count", context =>
        {
            AdminAuthorization.Add(context.Request.Headers.Authorization);
            if (_failure == "admin-denied")
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            }

            if (_failure == "count-not-a-number")
            {
                return context.Response.WriteAsync("\"many\"");
            }

            var count = Pages > 0 && CountAfterRead is { } later ? later : ReportedCount ?? Users.Count;
            return context.Response.WriteAsync(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        });
        app.MapGet("/admin/realms/learnstack/users", context =>
        {
            AdminAuthorization.Add(context.Request.Headers.Authorization);
            Interlocked.Increment(ref _pages);
            if (_failure == "users-redirect")
            {
                context.Response.StatusCode = StatusCodes.Status302Found;
                context.Response.Headers.Location = "https://elsewhere.example.test/";
                return Task.CompletedTask;
            }

            context.Response.ContentType = "application/json";
            if (_failure == "users-not-an-array")
            {
                return context.Response.WriteAsync("{}");
            }

            var first = int.Parse(context.Request.Query["first"]!, System.Globalization.CultureInfo.InvariantCulture);
            var max = int.Parse(context.Request.Query["max"]!, System.Globalization.CultureInfo.InvariantCulture);
            var page = Users.Skip(first).Take(max).Select(id => _failure == "user-without-id"
                ? (object)new { username = id }
                : new { id, username = "user-" + id });
            return context.Response.WriteAsync(JsonSerializer.Serialize(page));
        });
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
    }
}
