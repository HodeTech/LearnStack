using System.Net;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Docker.DotNet;
using Docker.DotNet.Models;
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
    internal TimeSpan StartupElapsed { get; private set; }
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
        var started = Stopwatch.GetTimestamp();
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
                .WithCreateParameterModifier(parameters => BindLoopback(parameters, "5432/tcp"))
                .Build();
            await _database.StartAsync(startup.Token);
            stage = "import_input";
            var tenantImport = _tenantRealmJson ?? await File.ReadAllTextAsync(RealmPath("learnstack"), startup.Token);
            var hubImport = await File.ReadAllBytesAsync(RealmPath("learnstack-hub"), startup.Token);
            _keycloak = new ContainerBuilder(Image)
                .WithLogger(NullLogger.Instance)
                .WithNetwork(_network)
                .WithPortBinding(8443, true)
                .WithCreateParameterModifier(parameters => BindLoopback(parameters, "8443/tcp"))
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
            stage = "loopback_bindings";
            await AssertLoopbackBindingsAsync(startup.Token);
            stage = "discovery";
            using var client = KeycloakHttp.Create(BaseAddress, RootCertificate);
            using var discovery = await KeycloakHttp.JsonAsync(client, HttpMethod.Get,
                "realms/learnstack/.well-known/openid-configuration", cancellationToken: startup.Token);
            if (discovery.RootElement.GetProperty("issuer").GetString() != Issuer)
                throw new InvalidOperationException("Keycloak fixture issuer does not match the mapped endpoint.");
            startup.Token.ThrowIfCancellationRequested();
            StartupElapsed = Stopwatch.GetElapsedTime(started);
        }
        catch (Exception exception)
        {
            // Startup exceptions can include container configuration, credentials or raw provider logs.
            // Cleanup still attempts every owned resource before this closed diagnostic is emitted.
            var reason = StartupFailureReason(exception);
            var cleanup = "completed";
            try { await DisposeAsync(); } catch (Exception) { cleanup = "failed"; }
            throw new InvalidOperationException("Keycloak fixture startup failed at " + stage + "/" + reason
                + "; owned cleanup " + cleanup + ".");
        }
    }

    internal static string StartupFailureReason(Exception exception) => exception switch
    {
        ContainerNotRunningException => "container_exit",
        OperationCanceledException => "deadline",
        TimeoutException => "readiness_timeout",
        HttpRequestException => "http_transport",
        DockerApiException { StatusCode: HttpStatusCode.BadRequest } => "docker_bad_request",
        DockerApiException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => "docker_access_denied",
        DockerApiException { StatusCode: HttpStatusCode.NotFound } => "docker_not_found",
        DockerApiException { StatusCode: HttpStatusCode.Conflict } => "docker_conflict",
        DockerApiException { StatusCode: HttpStatusCode.TooManyRequests } => "docker_rate_limited",
        DockerApiException { StatusCode: HttpStatusCode.InternalServerError } => "docker_server_error",
        DockerApiException => "docker_api_error",
        ResourceReaperException => "resource_reaper",
        System.Net.Sockets.SocketException => "socket_error",
        UnauthorizedAccessException => "filesystem_access_denied",
        IOException => "filesystem_io",
        ArgumentException => "argument",
        InvalidOperationException => "invalid_state",
        NotSupportedException => "unsupported_operation",
        KeyNotFoundException => "missing_mapping",
        _ => "provider_error",
    };

    internal async Task<KeycloakAdminClient> CreateAdminClientAsync(string realm = "learnstack")
    {
        var client = new KeycloakAdminClient(BaseAddress, RootCertificate, CheckedRealm(realm), AuthenticateAdministratorAsync);
        try { await client.AuthenticateAsync(); return client; }
        catch { client.Dispose(); throw; }
    }

    private async Task<KeycloakAdminCredential> AuthenticateAdministratorAsync(CancellationToken cancellationToken)
    {
        using var client = KeycloakHttp.Create(BaseAddress, RootCertificate);
        // Only the isolated master administrator uses admin-cli's password flow. Tenant proofs use code/S256.
        using var response = await KeycloakHttp.SendAsync(client, HttpMethod.Post,
            "realms/master/protocol/openid-connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = AdminUsername,
                ["password"] = _adminPassword,
            }), cancellationToken);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException("Keycloak fixture administrator authentication failed.");
        using var token = await KeycloakHttp.ReadJsonAsync(response, cancellationToken);
        var accessToken = token.RootElement.GetProperty("access_token").GetString();
        var expiresIn = token.RootElement.GetProperty("expires_in").GetInt32();
        if (string.IsNullOrEmpty(accessToken) || expiresIn <= 5)
            throw new InvalidOperationException("Keycloak fixture administrator credential is invalid.");
        return new KeycloakAdminCredential(accessToken, TimeSpan.FromSeconds(expiresIn));
    }

    private static void BindLoopback(CreateContainerParameters parameters, string port)
    {
        var bindings = parameters.HostConfig?.PortBindings;
        if (bindings is null || bindings.Count != 1 || !bindings.TryGetValue(port, out var values) || values.Count != 1)
            throw new InvalidOperationException("Keycloak fixture port inventory is invalid.");
        values[0].HostIP = "127.0.0.1";
    }

    internal async Task AssertLoopbackBindingsAsync(CancellationToken cancellationToken = default)
    {
        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
        foreach (var (container, port) in new[] { (_database as IContainer, "5432/tcp"), (_keycloak, "8443/tcp") })
        {
            if (container is null) throw new InvalidOperationException("Keycloak fixture container is not initialized.");
            var actual = await docker.Containers.InspectContainerAsync(container.Id, cancellationToken);
            if (!LoopbackBindings(actual, port))
                throw new InvalidOperationException("Keycloak fixture published port is not exclusively loopback.");
        }
    }

    internal static bool LoopbackBindings(ContainerInspectResponse container, string port) =>
        container.HostConfig?.PortBindings is { Count: 1 } configured
        && configured.TryGetValue(port, out var intended) && intended.Count == 1 && intended[0].HostIP == "127.0.0.1"
        && container.NetworkSettings?.Ports is { } actual
        && actual.All(entry => entry.Key == port || entry.Value is null or { Count: 0 })
        && actual.TryGetValue(port, out var published) && published is { Count: 1 }
        && published[0].HostIP == "127.0.0.1" && ushort.TryParse(published[0].HostPort, out var mapped) && mapped != 0;

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
