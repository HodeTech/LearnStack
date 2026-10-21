using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging.Abstractions;
using LearnStack.Tools.Seeder;
using Testcontainers.PostgreSql;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>A separate provider database; never a substitute for application-role isolation tests.</summary>
public sealed class KeycloakFixture : IAsyncLifetime, IAsyncDisposable
{
    internal const string Image = "quay.io/keycloak/keycloak:26.6.2";
    internal const string WebClientSecret = "keycloak-fixture-web-secret";
    internal const string DemoPassword = "demo-dev-secret";
    private const string AdminUsername = "fixture-admin";
    private readonly string _adminPassword = Guid.NewGuid().ToString("N");
    private readonly string _databasePassword = Guid.NewGuid().ToString("N");
    private readonly string? _tenantRealmJson;
    private INetwork? _network;
    private PostgreSqlContainer? _database;
    private IContainer? _keycloak;
    private KeycloakTls? _tls;
    private HttpMessageHandler? _readinessHandler;
    private int _disposed;

    public KeycloakFixture() { }
    private KeycloakFixture(string? tenantRealmJson) => _tenantRealmJson = tenantRealmJson;

    internal Uri BaseAddress { get; private set; } = null!; // Set only after successful container startup.
    internal string Issuer => IssuerFor("learnstack");
    internal string IssuerFor(string realm) => new Uri(BaseAddress, "realms/" + CheckedRealm(realm)).AbsoluteUri;

    internal static string CheckedRealm(string realm) => realm is "learnstack" or "learnstack-hub"
        ? realm : throw new InvalidOperationException("Keycloak fixture realm is outside the closed provider boundary.");
    internal X509Certificate2 RootCertificate => _tls?.RootCertificate
        ?? throw new InvalidOperationException("Keycloak fixture certificate authority is not initialized.");

    internal static async Task<KeycloakFixture> StartAsync(string? tenantRealmJson = null)
    {
        var fixture = new KeycloakFixture(tenantRealmJson);
        await fixture.InitializeAsync();
        return fixture;
    }

    public async Task InitializeAsync()
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stage = "certificate";
        try
        {
            _tls = new KeycloakTls();
            _readinessHandler = KeycloakHttp.CreateHandler(_tls.RootCertificate);
            stage = "network";
            _network = new NetworkBuilder().WithLogger(NullLogger.Instance).Build();
            await _network.CreateAsync(startup.Token);
            stage = "database";
            _database = new PostgreSqlBuilder("postgres:18.4-alpine")
                .WithLogger(NullLogger.Instance)
                .WithNetwork(_network)
                .WithNetworkAliases("identity-database")
                .WithDatabase("keycloak")
                .WithUsername("keycloak")
                .WithPassword(_databasePassword)
                .Build();
            await _database.StartAsync(startup.Token);
            stage = "import_input";
            var tenantImport = _tenantRealmJson ?? await File.ReadAllTextAsync(RealmPath("learnstack"), startup.Token);
            var hubImport = await File.ReadAllBytesAsync(RealmPath("learnstack-hub"), startup.Token);
            _keycloak = new ContainerBuilder(Image)
                .WithLogger(NullLogger.Instance)
                .WithNetwork(_network)
                .WithPortBinding(8443, true)
                .WithEnvironment("KC_DB", "postgres")
                .WithEnvironment("KC_DB_URL", "jdbc:postgresql://identity-database:5432/keycloak")
                .WithEnvironment("KC_DB_USERNAME", "keycloak")
                .WithEnvironment("KC_DB_PASSWORD", _databasePassword)
                .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", AdminUsername)
                .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", _adminPassword)
                .WithEnvironment("KEYCLOAK_WEB_CLIENT_SECRET", WebClientSecret)
                // byte[] destinations are files. Testcontainers' string/string overload treats the target as a directory.
                .WithResourceMapping(Encoding.UTF8.GetBytes(tenantImport), "/opt/keycloak/data/import/learnstack-realm.json")
                .WithResourceMapping(hubImport, "/opt/keycloak/data/import/learnstack-hub-realm.json")
                .WithResourceMapping(_tls.ServerCertificatePem, "/opt/keycloak/conf/fixture-cert.pem")
                .WithResourceMapping(_tls.ServerPrivateKeyPem, "/opt/keycloak/conf/fixture-key.pem", 1000, 0,
                    UnixFileModes.UserRead | UnixFileModes.UserWrite)
                .WithCommand("start-dev", "--import-realm", "--log-level=ERROR", "--http-enabled=false", "--https-port=8443",
                    "--https-certificate-file=/opt/keycloak/conf/fixture-cert.pem", "--https-certificate-key-file=/opt/keycloak/conf/fixture-key.pem")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                    .ForPort(8443).UsingTls(true).UsingHttpMessageHandler(_readinessHandler)
                    .ForPath("/realms/learnstack/.well-known/openid-configuration")
                    .ForStatusCode(HttpStatusCode.OK)))
                .Build();
            stage = "provider_start";
            await _keycloak.StartAsync(startup.Token);
            stage = "mapped_endpoint";
            BaseAddress = new UriBuilder(Uri.UriSchemeHttps, _keycloak.Hostname,
                _keycloak.GetMappedPublicPort(8443)).Uri;
            if (!BaseAddress.IsLoopback) throw new InvalidOperationException("Keycloak fixture endpoint is not loopback.");
            stage = "discovery";
            using var client = KeycloakHttp.Create(BaseAddress, RootCertificate);
            using var discovery = await KeycloakHttp.JsonAsync(client, HttpMethod.Get,
                "realms/learnstack/.well-known/openid-configuration");
            if (discovery.RootElement.GetProperty("issuer").GetString() != Issuer)
                throw new InvalidOperationException("Keycloak fixture issuer does not match the mapped endpoint.");
        }
        catch (Exception exception)
        {
            // Startup exceptions can include container configuration, credentials or raw provider logs.
            // Cleanup still attempts every owned resource before this closed diagnostic is emitted.
            var reason = exception switch
            {
                ContainerNotRunningException => "container_exit",
                OperationCanceledException => "deadline",
                TimeoutException => "readiness_timeout",
                HttpRequestException => "http_transport",
                ArgumentException => "argument",
                InvalidOperationException => "invalid_state",
                NotSupportedException => "unsupported_operation",
                KeyNotFoundException => "missing_mapping",
                _ => "provider_error",
            };
            await DisposeAsync();
            throw new InvalidOperationException("Keycloak fixture startup failed at " + stage + "/" + reason + " after owned cleanup.");
        }
    }

    internal async Task<KeycloakAdminClient> CreateAdminClientAsync(string realm = "learnstack")
    {
        realm = CheckedRealm(realm);
        using var client = KeycloakHttp.Create(BaseAddress, RootCertificate);
        // Only the isolated master administrator uses admin-cli's password flow. Tenant proofs use code/S256.
        using var response = await KeycloakHttp.SendAsync(client, HttpMethod.Post,
            "realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = AdminUsername,
                ["password"] = _adminPassword,
            }));
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Keycloak fixture administrator authentication failed.");
        using var token = await KeycloakHttp.ReadJsonAsync(response);
        return new KeycloakAdminClient(BaseAddress, RootCertificate, realm,
            token.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Keycloak fixture administrator credential is missing."));
    }

    internal KeycloakBrowserFlow Browser(string realm = "learnstack") => new(BaseAddress, RootCertificate, CheckedRealm(realm));

    // The child owns this pipe. Bootstrap credentials have no public projection or diagnostic record.
    internal async Task WriteReconciliationInputAsync(Stream destination, string scenario,
        CancellationToken cancellationToken)
    {
        using var desired = JsonDocument.Parse(await File.ReadAllTextAsync(RealmPath("learnstack"), cancellationToken));
        await JsonSerializer.SerializeAsync(destination, new
        {
            baseUrl = BaseAddress.AbsoluteUri,
            expectedIssuer = Issuer,
            adminUsername = AdminUsername,
            adminPassword = _adminPassword,
            webSecret = WebClientSecret,
            manifest = SeedIdentityManifest.Create(),
            desired = desired.RootElement,
            ca = RootCertificate.ExportCertificatePem(),
            scenario,
        }, JsonSerializerOptions.Web, cancellationToken);
    }

    internal static string RealmPath(string realm) => Path.Combine(RepositoryRoot(), "infra", "keycloak", "realms", realm + ".json");

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !(File.Exists(Path.Combine(directory.FullName, "CLAUDE.md"))
            && Directory.Exists(Path.Combine(directory.FullName, "infra", "keycloak", "realms"))))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Keycloak fixture repository root is missing.");
    }

    public async Task DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var failed = false;
        if (_keycloak is not null)
            try { await _keycloak.DisposeAsync(); } catch (Exception) { failed = true; }
        if (_database is not null)
            try { await _database.DisposeAsync(); } catch (Exception) { failed = true; }
        if (_network is not null)
            try { await _network.DisposeAsync(); } catch (Exception) { failed = true; }
        try { _readinessHandler?.Dispose(); } catch (Exception) { failed = true; }
        try { _tls?.Dispose(); } catch (Exception) { failed = true; }
        if (failed) throw new InvalidOperationException("Keycloak fixture owned cleanup failed.");
    }

    async ValueTask IAsyncDisposable.DisposeAsync() => await DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class SharedKeycloakRealm : ICollectionFixture<KeycloakFixture>
{
    public const string Name = "tenant-keycloak-realm";
}
