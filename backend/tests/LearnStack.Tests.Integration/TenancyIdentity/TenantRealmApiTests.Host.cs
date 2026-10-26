using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantRealmApiTests
{
    private HttpClient Client(SeedTenant tenant, out TenantRealmApiObservation observation,
        X509Certificate2? authority = null, string? issuer = null, PublicReadFixture? sourceDatabase = null)
    {
        var observed = new TenantRealmApiObservation();
        observation = observed;
        return (sourceDatabase ?? _database).ClientFor(tenant.Host, services =>
        {
            // No alternate retriever, configuration manager, JWT handler or claims source.
            services.Replace(ServiceDescriptor.Singleton(_ => Backchannel(authority ?? _provider.RootCertificate)));
            // Retain the fixture's physical ReadOnly/app-role observer inside this outer context observer.
            services.AddScoped<ObservedPublicConfigurationReader>();
            services.AddScoped<IPublicTenantConfigurationReader>(serviceProvider => new TenantRealmContextReader(
                serviceProvider.GetRequiredService<ObservedPublicConfigurationReader>(),
                serviceProvider.GetRequiredService<ITenantContext>(), serviceProvider.GetRequiredService<IUnitOfWork>(), observed));
        }, builder => builder.UseSetting(TenantAuthenticationOptions.IssuerConfigurationKey, issuer ?? _provider.Issuer));
    }

    private static TenantAuthenticationBackchannel Backchannel(X509Certificate2 authority)
    {
        var handler = KeycloakHttp.CreateHandler(authority);
        // An injected handler cannot relax the production transport bounds.
        handler.AllowAutoRedirect = true;
        handler.UseCookies = true;
        handler.ConnectTimeout = TimeSpan.FromMinutes(1);
        var backchannel = new TenantAuthenticationBackchannel(handler);
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
        handler.ConnectTimeout.Should().Be(TenantAuthenticationBackchannel.Timeout);
        handler.SslOptions.RemoteCertificateValidationCallback.Should().BeNull();
        backchannel.Client.Timeout.Should().Be(TenantAuthenticationBackchannel.Timeout);
        backchannel.Client.MaxResponseContentBufferSize.Should().Be(TenantAuthenticationBackchannel.MaximumDocumentBytes);
        return backchannel;
    }

    private Task<KeycloakTokenExchange> TokensAsync(SeedIdentity identity) =>
        ExchangeAsync(identity.Username, Callback(identity.Tenant));

    private async Task<KeycloakTokenExchange> ExchangeAsync(string username, string callback,
        string realm = "learnstack", string clientId = "learnstack-web", string? secret = KeycloakFixture.WebClientSecret)
    {
        using var browser = _provider.Browser(realm);
        var authorization = await browser.AuthorizeAsync(username, KeycloakFixture.DemoPassword, callback,
            KeycloakPkce.S256, clientId);
        authorization.Authorized.Should().BeTrue("the actual provider must complete the code/S256 flow");
        var tokens = await browser.RedeemAsync(authorization, secret);
        tokens.StatusCode.Should().Be(HttpStatusCode.OK);
        string.IsNullOrEmpty(tokens.AccessToken).Should().BeFalse("the provider must issue an access credential");
        string.IsNullOrEmpty(tokens.IdToken).Should().BeFalse("the provider must issue an identity credential");
        return tokens;
    }

    private static string Callback(SeedTenant tenant) => "https://" + tenant.Host + ":3000/api/auth/callback/keycloak";

    private static Task<HttpResponseMessage> Send(HttpClient client, SeedTenant tenant, string? token = null,
        string path = TenantAuthenticationHttpRequest.AccountPath) =>
        TenantAuthenticationHttpRequest.Send(client, tenant.Host, path, token);

    private static Task<HttpStatusCode> CreateUserAsync(KeycloakAdminClient admin, string username,
        IReadOnlyDictionary<string, string[]>? attributes = null) => admin.PostAsync("users", new
        {
            username,
            email = username,
            emailVerified = true,
            enabled = true,
            firstName = "API",
            lastName = "Fixture",
            requiredActions = Array.Empty<string>(),
            attributes = attributes ?? new Dictionary<string, string[]>(),
            credentials = new[] { new { type = "password", value = KeycloakFixture.DemoPassword, temporary = false } },
        });

    private static string UserPath(System.Text.Json.JsonElement user)
    {
        var id = user.GetProperty("id").GetString();
        Guid.TryParse(id, out _).Should().BeTrue("the provider must return one concrete account identifier");
        return "users/" + id;
    }

    // A supplied attribute map replaces profile values in Keycloak 26.6.2.
    private static object UserAttributeUpdate(System.Text.Json.JsonElement user, object attributes) => new
    {
        username = user.GetProperty("username"),
        email = user.GetProperty("email"),
        firstName = user.GetProperty("firstName"),
        lastName = user.GetProperty("lastName"),
        attributes,
    };

    private static async Task DeleteOwnedUserAsync(KeycloakAdminClient admin, string username)
    {
        var owned = await admin.UserAsync(username);
        (owned.GetProperty("username").GetString() == username).Should().BeTrue(
            "cleanup may delete only the exact unrelated account created by this case");
        (await admin.DeleteAsync(UserPath(owned))).Should().Be(HttpStatusCode.NoContent);
    }
}

internal sealed class TenantRealmApiObservation
{
    internal ConcurrentQueue<TenantRealmReadContext> Reads { get; } = new();
}

internal sealed record TenantRealmReadContext(Guid Tenant, Guid? Organization, Guid? Actor,
    TenantContextOrigin? Origin, Guid HostTenant, Guid? HostOrganization);

/// <summary>Observes existing context and GUCs without supplying an identity, host or persistence answer.</summary>
internal sealed class TenantRealmContextReader(ObservedPublicConfigurationReader reader, ITenantContext context,
    IUnitOfWork unit, TenantRealmApiObservation observation) : IPublicTenantConfigurationReader
{
    public async Task<Result<PublicTenantConfiguration>> ReadAsync(CancellationToken cancellationToken)
    {
        context.IsResolved.Should().BeTrue();
        context.HostScope.Should().NotBeNull();
        await SchemaQueries.AssertApplicationRoleAsync(unit.Connection, unit.Transaction);
        await using (var command = new NpgsqlCommand("""
                         SELECT current_setting('app.tenant_id', true), current_setting('app.organization_id', true)
                         """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction))
        await using (var settings = await command.ExecuteReaderAsync(cancellationToken))
        {
            (await settings.ReadAsync(cancellationToken)).Should().BeTrue();
            settings.GetString(0).Should().Be(context.TenantId.Value.ToString("D"));
            settings.GetString(1).Should().Be(context.OrganizationId?.Value.ToString("D") ?? "");
        }
        var snapshot = new TenantRealmReadContext(context.TenantId.Value, context.OrganizationId?.Value,
            context.UserId?.Value, context.Origin, context.HostScope!.TenantId.Value, context.HostScope.OrganizationId?.Value);
        var result = await reader.ReadAsync(cancellationToken);
        observation.Reads.Enqueue(snapshot);
        return result;
    }
}

/// <summary>Owned loopback transport failure with a connection witness; it never stops a shared provider.</summary>
internal sealed class UnavailableTenantIssuer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _owner;
    private int _connections;

    internal UnavailableTenantIssuer()
    {
        _listener.Start();
        Issuer = "https://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/realms/learnstack";
        _owner = RefuseAsync();
    }

    internal string Issuer { get; }
    internal int Connections => Volatile.Read(ref _connections);

    private async Task RefuseAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var connection = await _listener.AcceptTcpClientAsync(_stop.Token);
                Interlocked.Increment(ref _connections);
                connection.LingerState = new LingerOption(true, 0);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        await _owner;
        _stop.Dispose();
    }
}
