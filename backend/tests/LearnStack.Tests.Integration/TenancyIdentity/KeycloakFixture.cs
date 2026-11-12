using System.Net;
using System.Diagnostics;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
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
public sealed partial class KeycloakFixture : IAsyncLifetime, IAsyncDisposable
{
    internal const string Image = "quay.io/keycloak/keycloak:26.6.2";
    internal const string WebClientSecret = "keycloak-fixture-web-secret";
    internal const string EventReaderSecret = "keycloak-fixture-event-reader-secret";
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
    private bool _callbackTrust = true;

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
    internal string CallbackCertificate => Encoding.ASCII.GetString(_tls!.CallbackCertificatePem);
    internal string CallbackPrivateKey => Encoding.ASCII.GetString(_tls!.CallbackPrivateKeyPem);

    internal static async Task<KeycloakFixture> StartAsync(string? tenantRealmJson = null, bool callbackTrust = true)
    {
        var fixture = new KeycloakFixture(tenantRealmJson) { _callbackTrust = callbackTrust };
        await fixture.InitializeAsync();
        return fixture;
    }

    public async Task InitializeAsync()
    {
        var started = Stopwatch.GetTimestamp();
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var diagnostics = new KeycloakStartupDiagnostics();
        var stage = "certificate";
        diagnostics.Mark(stage);
        try
        {
            _tls = new KeycloakTls();
            _readinessHandler = diagnostics.Observe(KeycloakHttp.CreateHandler(_tls.RootCertificate));
            stage = "network";
            diagnostics.Mark(stage);
            _network = new NetworkBuilder().WithLogger(NullLogger.Instance).Build();
            await _network.CreateAsync(startup.Token);
            await StartInvitationMailAsync(startup.Token);
            stage = "database";
            diagnostics.Mark(stage);
            _database = new PostgreSqlBuilder("postgres:18.4-alpine")
                .WithLogger(NullLogger.Instance)
                .WithNetwork(_network)
                .WithNetworkAliases("identity-database")
                .WithDatabase("keycloak")
                .WithUsername("keycloak")
                .WithPassword(_databasePassword)
                .WithCreateParameterModifier(parameters =>
                {
                    ContainerLoopbackPorts.Configure(parameters, "5432/tcp");
                    stage = "database_create_request";
                    diagnostics.Mark(stage);
                })
                .Build();
            // These are last-reached SDK milestones, not exact failing HTTP operations.
            _database.Created += (_, _) => { stage = "database_start"; diagnostics.Mark(stage); };
            _database.Starting += (_, _) => { stage = "database_readiness"; diagnostics.Mark(stage); };
            _database.Started += (_, _) => { stage = "database_started"; diagnostics.Mark(stage); };
            await _database.StartAsync(startup.Token);
            stage = "import_input";
            diagnostics.Mark(stage);
            var tenantImport = _tenantRealmJson ?? await File.ReadAllTextAsync(RealmPath("learnstack"), startup.Token);
            tenantImport = ConfigureInvitationMailRealm(tenantImport);
            var hubImport = await File.ReadAllBytesAsync(RealmPath("learnstack-hub"), startup.Token);
            _keycloak = BuildProvider(tenantImport, hubImport, _callbackTrust);
            _keycloak.Created += (_, _) => { stage = "provider_created"; diagnostics.Mark(stage); };
            _keycloak.Starting += (_, _) => { stage = "provider_starting"; diagnostics.Mark(stage); };
            _keycloak.Started += (_, _) => { stage = "provider_started"; diagnostics.Mark(stage); };
            stage = "provider_start";
            diagnostics.Mark(stage);
            await _keycloak.StartAsync(startup.Token);
            stage = "mapped_endpoint";
            diagnostics.Mark(stage);
            BaseAddress = new UriBuilder(Uri.UriSchemeHttps, _keycloak.Hostname,
                _keycloak.GetMappedPublicPort(8443)).Uri;
            if (!BaseAddress.IsLoopback) throw new InvalidOperationException("Keycloak fixture endpoint is not loopback.");
            stage = "loopback_bindings";
            diagnostics.Mark(stage);
            await AssertLoopbackBindingsAsync(startup.Token);
            await AssertInvitationMailBindingsAsync(startup.Token);
            stage = "discovery";
            diagnostics.Mark(stage);
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
            var startupCancellationRequested = startup.IsCancellationRequested;
            // State is the SDK's cached observation, not a new Docker inspection or a live-state witness.
            var observation = diagnostics.Snapshot(_keycloak?.State);
            // Startup exceptions can include container configuration, credentials or raw provider logs.
            // Cleanup still attempts every owned resource before this closed diagnostic is emitted.
            var cleanup = "completed";
            try { await DisposeAsync(); } catch (Exception) { cleanup = "failed"; }
            var reason = KeycloakStartupFailure.Classify(exception);
            var failure = new InvalidOperationException("Keycloak fixture startup failed at " + stage + "/" + reason
                + "; owned cleanup " + cleanup
                + "; startup cancellation requested " + (startupCancellationRequested ? "true" : "false") + "."
                + " " + observation.ToSentence());
            failure.Data[KeycloakStartupDiagnostics.DataKey] = observation;
            throw failure;
        }
    }

    private IContainer BuildProvider(string tenantImport, byte[] hubImport, bool callbackTrust, int? publicPort = null)
    {
        if (_network is null || _tls is null || _readinessHandler is null)
            throw new InvalidOperationException("Keycloak fixture provider dependencies are unavailable.");
        var builder = new ContainerBuilder(Image)
                .WithLogger(NullLogger.Instance)
                .WithNetwork(_network!)
                .WithPortBinding(8443, true)
                .WithCreateParameterModifier(parameters =>
                {
                    ContainerLoopbackPorts.Configure(parameters, "8443/tcp");
                    if (publicPort is not null)
                        parameters.HostConfig!.PortBindings!["8443/tcp"][0].HostPort =
                            publicPort.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    parameters.HostConfig!.ExtraHosts = ["host.docker.internal:host-gateway"];
                })
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
                .WithResourceMapping(_tls!.ServerCertificatePem, "/opt/keycloak/conf/fixture-cert.pem")
                .WithResourceMapping(_tls!.ServerPrivateKeyPem, "/opt/keycloak/conf/fixture-key.pem", 1000, 0,
                    UnixFileModes.UserRead | UnixFileModes.UserWrite)
                .WithCommand("start-dev", "--import-realm", "--log-level=ERROR", "--http-enabled=false", "--https-port=8443",
                    "--https-certificate-file=/opt/keycloak/conf/fixture-cert.pem", "--https-certificate-key-file=/opt/keycloak/conf/fixture-key.pem")
                .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request
                    .ForPort(8443).UsingTls(true).UsingHttpMessageHandler(_readinessHandler!)
                    .ForPath("/realms/learnstack/.well-known/openid-configuration")
                    .ForStatusCode(HttpStatusCode.OK)));
        if (callbackTrust)
            builder = builder.WithResourceMapping(Encoding.ASCII.GetBytes(_tls!.RootCertificate.ExportCertificatePem()),
                "/opt/keycloak/conf/truststores/callback-root.pem");
        return builder.Build();
    }

    internal async Task RecreateWithCallbackTrustAsync()
    {
        if (_keycloak is null || _disposed != 0 || _callbackTrust)
            throw new InvalidOperationException("Keycloak fixture provider recreation is unavailable.");
        try
        {
            var previousAddress = BaseAddress;
            var publicPort = _keycloak.GetMappedPublicPort(8443);
            using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var tenantImport = _tenantRealmJson ?? await File.ReadAllTextAsync(RealmPath("learnstack"), startup.Token);
            tenantImport = ConfigureInvitationMailRealm(tenantImport);
            var hubImport = await File.ReadAllBytesAsync(RealmPath("learnstack-hub"), startup.Token);
            await _keycloak.DisposeAsync();
            _keycloak = null;
            _keycloak = BuildProvider(tenantImport, hubImport, callbackTrust: true, publicPort);
            await _keycloak.StartAsync(startup.Token);
            var address = new UriBuilder(Uri.UriSchemeHttps, _keycloak.Hostname, _keycloak.GetMappedPublicPort(8443)).Uri;
            if (address != previousAddress || !address.IsLoopback)
                throw new InvalidOperationException("Keycloak fixture provider endpoint changed.");
            await AssertLoopbackBindingsAsync(startup.Token);
            using var client = KeycloakHttp.Create(BaseAddress, RootCertificate);
            using var discovery = await KeycloakHttp.JsonAsync(client, HttpMethod.Get,
                "realms/learnstack/.well-known/openid-configuration", cancellationToken: startup.Token);
            if (discovery.RootElement.GetProperty("issuer").GetString() != Issuer)
                throw new InvalidOperationException("Keycloak fixture issuer changed.");
            _callbackTrust = true;
        }
        catch (Exception error) when (error.GetType() != typeof(OutOfMemoryException)
            && error.GetType() != typeof(StackOverflowException))
        {
            // The owning fixture still disposes any partially created replacement.
            // Provider/SDK exceptions may carry configuration or credentials.
            throw new InvalidOperationException("Keycloak fixture provider recreation failed.");
        }
    }

    internal async Task<KeycloakAdminClient> CreateAdminClientAsync(string realm = "learnstack")
    {
        var client = new KeycloakAdminClient(BaseAddress, RootCertificate, CheckedRealm(realm), AuthenticateAdministratorAsync);
        try { await client.AuthenticateAsync(); return client; }
        catch { client.Dispose(); throw; }
    }

    // A fresh isolated master issuance observes the provider's integer-second
    // clock. This scalar is test timing only, never a token-authority witness.
    // Extra master sessions belong to this fixture's actual container cleanup.
    internal async Task<long> ObserveProviderUtcAsync(CancellationToken cancellationToken)
    {
        var credential = await AuthenticateAdministratorAsync(cancellationToken);
        try
        {
            var parts = credential.AccessToken.Split('.');
            if (parts.Length != 3) throw new InvalidOperationException();
            using var payload = JsonDocument.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.DecodeBytes(parts[1]));
            if (payload.RootElement.ValueKind != JsonValueKind.Object
                || payload.RootElement.EnumerateObject().Count(property => property.NameEquals("iat")) != 1
                || !payload.RootElement.TryGetProperty("iat", out var issued)
                || issued.ValueKind != JsonValueKind.Number || !issued.TryGetInt64(out var seconds)
                || seconds <= 0) throw new InvalidOperationException();
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToUnixTimeMilliseconds();
        }
        catch (Exception error) when (error.GetType() != typeof(OutOfMemoryException)
            && error.GetType() != typeof(StackOverflowException))
        { throw new InvalidOperationException("Keycloak fixture clock observation is invalid."); }
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

    internal async Task AssertLoopbackBindingsAsync(CancellationToken cancellationToken = default)
    {
        using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
        foreach (var (container, port) in new[] { (_database as IContainer, "5432/tcp"), (_keycloak, "8443/tcp") })
        {
            if (container is null) throw new InvalidOperationException("Keycloak fixture container is not initialized.");
            var actual = await docker.Containers.InspectContainerAsync(container.Id, cancellationToken);
            if (!ContainerLoopbackPorts.IsExclusive(actual, port))
                throw new InvalidOperationException("Keycloak fixture published port is not exclusively loopback.");
        }
    }

    internal KeycloakBrowserFlow Browser(string realm = "learnstack") => new(BaseAddress, RootCertificate, CheckedRealm(realm));

    // The child owns this pipe. Bootstrap credentials have no public projection or diagnostic record.
    internal async Task WriteReconciliationInputAsync(Stream destination, string scenario,
        CancellationToken cancellationToken, bool includeEventReader = false, string? backchannelUrl = null)
    {
        using var desired = JsonDocument.Parse(await File.ReadAllTextAsync(RealmPath("learnstack"), cancellationToken));
        var input = JsonSerializer.SerializeToNode(new
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
        }, JsonSerializerOptions.Web)!.AsObject();
        if (includeEventReader) input["eventReaderSecret"] = EventReaderSecret;
        if (backchannelUrl is not null)
            input["backchannel"] = JsonSerializer.SerializeToNode(new { enabled = true, url = backchannelUrl });
        await JsonSerializer.SerializeAsync(destination, input, JsonSerializerOptions.Web, cancellationToken);
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
        try { await DisposeInvitationMailAsync(); } catch (Exception) { failed = true; }
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
