using System.Text;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Microsoft.Extensions.Logging.Abstractions;
using LearnStack.SharedKernel.Identifiers;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Independent client and transaction-pool lanes over one fully migrated application database.</summary>
public sealed class AuthorizationPoolProgressFixture : IAsyncLifetime
{
    internal const int BusinessCapacity = 2;
    internal const int AuthorizationCapacity = 4;
    internal const int EvidenceCapacity = 2;
    internal const string AuthorizationAlias = "learnstack_identity_authorization";
    internal const string EvidenceAlias = "learnstack_authorization_evidence";
    internal const string PgBouncerImage = "edoburu/pgbouncer:v1.26.0-p0";
    private const string PostgresAlias = "authorization-postgres";
    private const string ConfigurationPath = "/tmp/authorization-progress.ini";
    private const string AuthenticationPath = "/tmp/authorization-progress-users.txt";
    private readonly IdentityMembershipFixture _membership = new();
    private INetwork? _network;
    private IContainer? _pgBouncer;
    private bool _databaseAttached;
    private string? _directConnectionString;

    internal string DirectConnectionString => _directConnectionString ?? _membership.Postgres.AppConnectionString;
    internal bool DatabasePublicationWasRemapped { get; private set; }

    internal string ConnectionString(bool transactionPooled, string lane, string applicationName)
    {
        var builder = new NpgsqlConnectionStringBuilder(DirectConnectionString)
        {
            ApplicationName = applicationName,
            Pooling = true,
            MaxPoolSize = lane == "business" ? BusinessCapacity
                : lane == "authorization" ? AuthorizationCapacity : EvidenceCapacity,
            MinPoolSize = 0,
            Multiplexing = false,
            Enlist = false,
            Timeout = 3,
            CommandTimeout = 3,
            CancellationTimeout = 1000,
            SslMode = SslMode.Disable,
        };
        if (transactionPooled)
        {
            var pooler = _pgBouncer ?? throw new InvalidOperationException("Pooler is unavailable.");
            builder.Host = pooler.Hostname;
            builder.Port = pooler.GetMappedPublicPort(6432);
            builder.Database = lane switch
            {
                "business" => new NpgsqlConnectionStringBuilder(DirectConnectionString).Database,
                "authorization" => AuthorizationAlias,
                "evidence" => EvidenceAlias,
                _ => throw new ArgumentOutOfRangeException(nameof(lane)),
            };
        }
        return builder.ConnectionString;
    }

    public async Task InitializeAsync()
    {
        using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var stage = "application-database";
        try
        {
            // This fixture applies every discovered migration chain and seeds actual committed memberships.
            await _membership.InitializeAsync();
            await SeedLifecycleAsync(startup.Token);
            stage = "network";
            _network = new NetworkBuilder().WithLogger(NullLogger.Instance).Build();
            await _network.CreateAsync(startup.Token);
            using (var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build())
            {
                await docker.Networks.ConnectNetworkAsync(_network.Name, new NetworkConnectParameters
                {
                    Container = _membership.Postgres.ContainerId,
                    EndpointConfig = new EndpointSettings { Aliases = [PostgresAlias] },
                }, startup.Token);
                _databaseAttached = true;
                // This external network operation may change a dynamic port mapping.
                // Reinspect the owned container instead of relying on the earlier Testcontainers observation.
                var database = await docker.Containers.InspectContainerAsync(_membership.Postgres.ContainerId, startup.Token);
                if (!ContainerLoopbackPorts.IsExclusive(database, "5432/tcp"))
                    throw new InvalidOperationException("Application database publication is not exclusively loopback.");
                if (database.NetworkSettings?.Ports is not { } ports
                    || !ports.TryGetValue("5432/tcp", out var bindings) || bindings is not { Count: 1 }
                    || !ushort.TryParse(bindings[0].HostPort, System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var actualPort) || actualPort == 0)
                    throw new InvalidOperationException("Application database publication is invalid.");
                var direct = new NpgsqlConnectionStringBuilder(_membership.Postgres.AppConnectionString);
                DatabasePublicationWasRemapped = direct.Port != actualPort;
                direct.Port = actualPort;
                _directConnectionString = direct.ConnectionString;
            }

            stage = "transaction-pooler";
            var app = new NpgsqlConnectionStringBuilder(DirectConnectionString);
            _pgBouncer = new ContainerBuilder(PgBouncerImage)
                .WithLogger(NullLogger.Instance)
                .WithNetwork(_network)
                .WithPortBinding(6432, true)
                .WithCreateParameterModifier(parameters => ContainerLoopbackPorts.Configure(parameters, "6432/tcp"))
                // The image's normal shell entrypoint prints generated credentials. Run only the pinned binary.
                .WithEntrypoint("/usr/bin/pgbouncer")
                .WithCommand(ConfigurationPath)
                .WithResourceMapping(Encoding.UTF8.GetBytes(Configuration(app.Database!)), ConfigurationPath)
                .WithResourceMapping(Encoding.UTF8.GetBytes($"\"{app.Username}\" \"{app.Password}\"\n"), AuthenticationPath)
                .WithWaitStrategy(Wait.ForUnixContainer().UntilInternalTcpPortIsAvailable(6432))
                .Build();
            await _pgBouncer.StartAsync(startup.Token);
            using (var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build())
            {
                var inspected = await docker.Containers.InspectContainerAsync(_pgBouncer.Id, startup.Token);
                if (!ContainerLoopbackPorts.IsExclusive(inspected, "6432/tcp"))
                    throw new InvalidOperationException("Pooler publication is not exclusively loopback.");
            }

            stage = "pinned-version";
            var version = await _pgBouncer.ExecAsync(["/usr/bin/pgbouncer", "--version"], startup.Token);
            if (version.ExitCode != 0 || !version.Stdout.Contains("PgBouncer 1.26.0", StringComparison.Ordinal))
                throw new InvalidOperationException("Pooler binary does not match its fixture pin.");

            stage = "same-database-role";
            foreach (var pooled in new[] { false, true })
                foreach (var lane in new[] { "business", "authorization", "evidence" })
                {
                    stage = "same-database-role-" + (pooled ? "transaction-" : "direct-") + lane;
                    var probeConnection = new NpgsqlConnectionStringBuilder(ConnectionString(pooled, lane, "progress-topology-check"))
                    { Pooling = false };
                    await using var connection = new NpgsqlConnection(probeConnection.ConnectionString);
                    stage += "-open";
                    await connection.OpenAsync(startup.Token);
                    stage = "same-database-role-" + (pooled ? "transaction-" : "direct-") + lane + "-probe";
                    await using var probe = new NpgsqlCommand("""
                        SELECT current_database() = @database AND current_user = 'learnstack_app'
                            AND NOT r.rolsuper AND NOT r.rolbypassrls
                        FROM pg_roles AS r WHERE r.rolname = current_user
                        """, connection);
                    probe.Parameters.AddWithValue("database", app.Database!);
                    if (await probe.ExecuteScalarAsync(startup.Token) is not true)
                        throw new InvalidOperationException("Pool lanes do not share the guarded application database and role.");
                }
        }
        catch (Exception exception)
        {
            var cleanup = "completed";
            try { await DisposeAsync(); } catch { cleanup = "failed"; }
            // Container SDK failures may carry copied configuration, passwords or raw logs.
            var reason = exception switch
            {
                PostgresException postgres => "postgres-" + postgres.SqlState,
                NpgsqlException { InnerException: System.Net.Sockets.SocketException socket } => "socket-" + socket.SocketErrorCode,
                NpgsqlException { InnerException: IOException { InnerException: System.Net.Sockets.SocketException socket } }
                    => "stream-socket-" + socket.SocketErrorCode,
                NpgsqlException { InnerException: TimeoutException } => "database-timeout",
                NpgsqlException { InnerException: IOException } => "database-stream",
                NpgsqlException => "database-connection",
                TimeoutException => "timeout",
                OperationCanceledException => "cancelled",
                InvalidOperationException => "invalid-topology",
                _ => "unexpected-failure",
            };
            throw new InvalidOperationException($"Authorization progress fixture failed at {stage}/{reason}; owned cleanup {cleanup}.");
        }
    }

    private async Task SeedLifecycleAsync(CancellationToken cancellationToken)
    {
        await using var app = new NpgsqlConnection(DirectConnectionString);
        await app.OpenAsync(cancellationToken);
        await using var transaction = await app.BeginTransactionAsync(cancellationToken);
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await using var seed = new NpgsqlCommand("""
            INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version)
            VALUES(@tenant,'authorization-progress','Authorization progress','Trial',clock_timestamp(),@actor,0);
            INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
            VALUES(@organization,@tenant,'progress-branch','Progress branch','Active',clock_timestamp(),@actor,0);
            """, app, transaction);
        seed.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        seed.Parameters.AddWithValue("organization", SchemaFixture.OrgA1);
        seed.Parameters.AddWithValue("actor", UserId.SystemActor.Value);
        await seed.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static string Configuration(string database) => $$"""
        [databases]
        {{database}} = host={{PostgresAlias}} port=5432 dbname={{database}} user=learnstack_app pool_size={{BusinessCapacity}} max_db_connections={{BusinessCapacity}}
        {{AuthorizationAlias}} = host={{PostgresAlias}} port=5432 dbname={{database}} user=learnstack_app pool_size={{AuthorizationCapacity}} max_db_connections={{AuthorizationCapacity}}
        {{EvidenceAlias}} = host={{PostgresAlias}} port=5432 dbname={{database}} user=learnstack_app pool_size={{EvidenceCapacity}} max_db_connections={{EvidenceCapacity}}

        [pgbouncer]
        listen_addr = 0.0.0.0
        listen_port = 6432
        unix_socket_dir =
        auth_type = scram-sha-256
        auth_file = {{AuthenticationPath}}
        pool_mode = transaction
        max_client_conn = 16
        default_pool_size = 2
        reserve_pool_size = 0
        max_user_connections = {{BusinessCapacity + AuthorizationCapacity + EvidenceCapacity}}
        query_wait_timeout = 10
        server_connect_timeout = 3
        server_login_retry = 1
        server_idle_timeout = 0
        ignore_startup_parameters = extra_float_digits
        log_connections = 0
        log_disconnections = 0
        log_pooler_errors = 0
        log_stats = 0
        """;

    public async Task DisposeAsync()
    {
        var failures = new List<Exception>();
        if (_pgBouncer is { } pooler)
        {
            _pgBouncer = null;
            try { await pooler.DisposeAsync(); } catch (Exception exception) { failures.Add(exception); }
        }
        if (_databaseAttached && _network is { } attachedNetwork)
        {
            try
            {
                using var docker = TestcontainersSettings.OS.DockerEndpointAuthConfig.GetDockerClientBuilder(Guid.NewGuid()).Build();
                await docker.Networks.DisconnectNetworkAsync(attachedNetwork.Name, new NetworkDisconnectParameters
                {
                    Container = _membership.Postgres.ContainerId,
                    Force = true,
                });
                _databaseAttached = false;
            }
            catch (Exception exception) { failures.Add(exception); }
        }
        // Removing the owned database also releases its network endpoint if explicit detach failed.
        try { await _membership.DisposeAsync(); } catch (Exception exception) { failures.Add(exception); }
        if (_network is { } network)
        {
            _network = null;
            try { await network.DisposeAsync(); } catch (Exception exception) { failures.Add(exception); }
        }
        if (failures.Count != 0) throw new InvalidOperationException("Authorization progress fixture owned cleanup failed.");
    }
}
