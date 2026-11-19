using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Platform;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The offline tool as an operator runs it (ADR-0104 § 3): a child process, every value from its environment and one
/// line of standard input, a real operator issuer, a real database and a loopback new realm.
/// </summary>
/// <remarks>
/// What an operator sees is one fixed line of classes and counts, so every case also asserts that no credential,
/// token, issuer, authority or subject reached the output, and that a refusal left the issuer where it was.
/// </remarks>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentityAuthorityRebindToolTests(PublicReadFixture parent)
{
    private const string OperatorSubject = "operator-sub-9";
    private const string Grant = "platform.identity_authority.admin";

    [Fact]
    public async Task The_Tool_Rebinds_With_A_Token_On_Standard_Input_And_Prints_One_Line_Without_Identifiers()
    {
        await using var run = await Run.StartAsync(parent);

        var result = await run.ExecuteAsync(token: run.Token(), tokenOnInput: true);

        result.Code.Should().Be(0, result.Text);
        result.Out.Should().StartWith("rebind: outcome=applied correlation=rebind-")
            .And.Contain("active=2").And.Contain("unbootstrapped=0");
        result.Out.Trim().Split('\n').Should().ContainSingle("one fixed line");
        run.AssertNoValueLeaked(result);
        (await run.IssuerAsync()).Should().Be(run.NewIssuer);

        var correlation = result.Out.Split("correlation=")[1].Split(' ')[0].Trim();
        (await run.OperationsAsync(correlation)).Should().Equal(
            "identity.authority.rebind_attempt", "identity.authority.rebind");
    }

    [Fact]
    public async Task The_Tool_Rebinds_With_A_Token_In_The_Environment_And_A_Repeat_Is_Already_Applied()
    {
        await using var run = await Run.StartAsync(parent);

        (await run.ExecuteAsync(token: run.Token(), tokenOnInput: false)).Code.Should().Be(0);
        var repeat = await run.ExecuteAsync(token: run.Token(), tokenOnInput: false);

        repeat.Code.Should().Be(0);
        repeat.Out.Should().StartWith("rebind: outcome=already_applied");
    }

    [Theory]
    [InlineData("no-token", "refused_input")]
    [InlineData("argument", "refused_input")]
    [InlineData("no-realm-credential", "refused_input")]
    [InlineData("no-mode", "refused_input")]
    [InlineData("wrong-audience", "refused_operator_token_token_audience")]
    [InlineData("wrong-issuer", "refused_operator_token_token_issuer")]
    [InlineData("expired", "refused_operator_token_token_expired")]
    [InlineData("stale-authentication", "refused_operator_token_profile_auth_age")]
    [InlineData("single-factor", "refused_operator_token_profile_acr")]
    [InlineData("other-client", "refused_operator_token_profile_azp")]
    [InlineData("no-grant", "refused_operator_missing_permission")]
    [InlineData("another-grant", "refused_operator_missing_permission")]
    [InlineData("no-operator-issuer", "refused_operator_issuer_not_configured")]
    public async Task A_Refusal_Before_The_Database_Leaves_The_Issuer_And_Writes_No_Row(string scenario, string expected)
    {
        await using var run = await Run.StartAsync(parent);

        var result = await run.ExecuteAsync(scenario);

        result.Code.Should().Be(2);
        (result.Out + result.Error).Should().Contain(expected);
        run.AssertNoValueLeaked(result);
        (await run.IssuerAsync()).Should().Be(run.OldIssuer);
        (await run.RebindRowsAsync()).Should().Be(0, "a refusal at the gate is a counter and a log, never a durable row");
    }

    [Fact]
    public async Task A_Database_Role_That_Is_Not_The_Rebind_Role_Is_Refused()
    {
        await using var run = await Run.StartAsync(parent);

        var result = await run.ExecuteAsync(token: run.Token(), tokenOnInput: false, connection: run.AppConnection);

        result.Code.Should().Be(2);
        (result.Out + result.Error).Should().Contain("refused_input", "the connection string is not the rebind role's");
        run.AssertNoValueLeaked(result);
        (await run.IssuerAsync()).Should().Be(run.OldIssuer);
    }

    [Fact]
    public async Task A_New_Realm_Missing_An_Active_User_Refuses_With_Counts_And_Keeps_The_Intent_Row()
    {
        await using var run = await Run.StartAsync(parent, reportedUsers: 1);

        var result = await run.ExecuteAsync(token: run.Token(), tokenOnInput: false);

        result.Code.Should().Be(2);
        result.Out.Should().StartWith("rebind: outcome=refused_continuity_broken").And.Contain("missing_active=1");
        run.AssertNoValueLeaked(result);
        (await run.IssuerAsync()).Should().Be(run.OldIssuer);
        var correlation = result.Out.Split("correlation=")[1].Split(' ')[0].Trim();
        (await run.OperationsAsync(correlation)).Should().Equal("identity.authority.rebind_attempt");
    }

    [Fact]
    public async Task A_Rebind_Role_Session_Beside_A_Live_Application_Session_Is_Frozen_With_A_Count()
    {
        await using var run = await Run.StartAsync(parent);
        await using var application = (NpgsqlConnection)await PostgresFixture.OpenAsync(run.AppConnection);

        var result = await run.ExecuteAsync(token: run.Token(), tokenOnInput: false);

        result.Code.Should().Be(2);
        result.Out.Should().StartWith("rebind: outcome=refused_live_sessions").And.Contain("sessions=1");
        (await run.IssuerAsync()).Should().Be(run.OldIssuer);
    }

    private sealed record Result(int Code, string Out, string Error)
    {
        internal string Text => Out + Error;
    }

    private sealed class Run : IAsyncDisposable
    {
        private readonly IdentityPublicReadFixture _database;
        private readonly TenantAuthenticationIssuer _operator;
        private readonly FakeKeycloakRealm _realm;
        private readonly string[] _subjects;

        private Run(IdentityPublicReadFixture database, TenantAuthenticationIssuer @operator, FakeKeycloakRealm realm, string[] subjects)
        {
            _database = database;
            _operator = @operator;
            _realm = realm;
            _subjects = subjects;
        }

        internal string OldIssuer => _database.Registration.Issuer;

        internal string NewIssuer => _realm.Issuer;

        internal string AppConnection => _database.AppConnectionString;

        internal static async Task<Run> StartAsync(PublicReadFixture parent, int reportedUsers = 2)
        {
            var users = new[] { UserId.From(Guid.CreateVersion7()), UserId.From(Guid.CreateVersion7()) };
            var subjects = users.Select(user => "tool-proof-" + user.Value.ToString("N")).ToArray();
            var database = await IdentityPublicReadFixture.CreateAsync(
                parent, "https://old.example.test/realms/learnstack", users.Zip(subjects).ToDictionary(pair => pair.First, pair => pair.Second));
            var @operator = await TenantAuthenticationIssuer.StartAsync();
            var realm = await FakeKeycloakRealm.StartAsync(subjects.Take(reportedUsers));
            return new Run(database, @operator, realm, subjects);
        }

        internal string Token(params (string Name, object? Value)[] replacements)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var claims = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["iss"] = _operator.Issuer,
                ["sub"] = OperatorSubject,
                ["aud"] = "learnstack-platform",
                ["iat"] = now - 5,
                ["exp"] = now + 600,
                ["azp"] = "learnstack-operator",
                ["acr"] = "mfa",
                ["auth_time"] = now - 30,
            };
            foreach (var (name, value) in replacements) claims[name] = value;
            return _operator.Token(JsonSerializer.Serialize(claims));
        }

        internal Task<Result> ExecuteAsync(string scenario)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return scenario switch
            {
                "no-token" => ExecuteAsync(token: null, tokenOnInput: false),
                "argument" => ExecuteAsync(token: Token(), tokenOnInput: false, argument: "--force"),
                "no-realm-credential" => ExecuteAsync(token: Token(), tokenOnInput: false, realmCredential: false),
                "no-mode" => ExecuteAsync(token: Token(), tokenOnInput: false, mode: null),
                "wrong-audience" => ExecuteAsync(token: Token(("aud", "learnstack-api")), tokenOnInput: false),
                "wrong-issuer" => ExecuteAsync(token: Token(("iss", "https://elsewhere.example.test/realms/x")), tokenOnInput: false),
                "expired" => ExecuteAsync(token: Token(("exp", now - 3600), ("iat", now - 3700), ("auth_time", now - 3700)), tokenOnInput: false),
                "stale-authentication" => ExecuteAsync(token: Token(("auth_time", now - 7200)), tokenOnInput: false),
                "single-factor" => ExecuteAsync(token: Token(("acr", "pwd")), tokenOnInput: false),
                "other-client" => ExecuteAsync(token: Token(("azp", "learnstack-web")), tokenOnInput: false),
                "no-grant" => ExecuteAsync(token: Token(), tokenOnInput: false, grant: null),
                "another-grant" => ExecuteAsync(token: Token(), tokenOnInput: false, grant: "platform.killswitch.write"),
                "no-operator-issuer" => ExecuteAsync(token: Token(), tokenOnInput: false, operatorIssuer: false),
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
        }

        internal async Task<Result> ExecuteAsync(
            string? token, bool tokenOnInput, string? argument = null, bool realmCredential = true, string? mode = "Development",
            string? grant = Grant, bool operatorIssuer = true, string? connection = null)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("exec");
            start.ArgumentList.Add("--runtimeconfig");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.runtimeconfig.json"));
            start.ArgumentList.Add("--depsfile");
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.deps.json"));
            start.ArgumentList.Add(Assembly.Load("LearnStack.Tools.IdentityAuthorityRebind").Location);
            if (argument is not null) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach (var key in new[] { "PATH", "SystemRoot", "DOTNET_ROOT", "HOME" })
                if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.Environment["ConnectionStrings__IdentityRebind"] = connection ?? _database.IdentityRebindConnectionString;
            start.Environment["Identity__AuthorityId"] = _database.Registration.AuthorityId.Value.ToString("D");
            start.Environment["Authentication__Tenant__Issuer"] = _realm.Issuer;
            if (mode is not null) start.Environment["Deployment__Mode"] = mode;
            if (operatorIssuer)
            {
                start.Environment["PlatformOperator__Issuer"] = _operator.Issuer;
                start.Environment["PlatformOperator__AuthorizedParties__0"] = "learnstack-operator";
                start.Environment["PlatformOperator__AcceptedAcr__0"] = "mfa";
                if (grant is not null)
                {
                    start.Environment["PlatformOperator__Grants__0__Subject"] = OperatorSubject;
                    start.Environment["PlatformOperator__Grants__0__Permissions__0"] = grant;
                }
            }

            if (realmCredential)
            {
                start.Environment["LEARNSTACK_REBIND_REALM_CLIENT_ID"] = FakeKeycloakRealm.ClientId;
                start.Environment["LEARNSTACK_REBIND_REALM_CLIENT_SECRET"] = FakeKeycloakRealm.ClientSecret;
            }

            start.Environment["LEARNSTACK_REBIND_TICKET"] = "CHG-104";
            if (token is not null && !tokenOnInput) start.Environment["LEARNSTACK_OPERATOR_TOKEN"] = token;
            _token = token;

            using var child = new Process { StartInfo = start };
            child.Start().Should().BeTrue();
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            if (token is not null && tokenOnInput) await child.StandardInput.WriteLineAsync(token);
            child.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await child.WaitForExitAsync(deadline.Token); }
            finally
            {
                if (!child.HasExited)
                {
                    child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync(CancellationToken.None);
                }
            }

            return new Result(child.ExitCode, await output, await errors);
        }

        private string? _token;

        internal void AssertNoValueLeaked(Result result)
        {
            var forbidden = new List<string>
            {
                FakeKeycloakRealm.ClientSecret, _database.IdentityRebindConnectionString, _database.AppConnectionString,
                OldIssuer, NewIssuer, _operator.Issuer, _database.Registration.AuthorityId.Value.ToString("D"), OperatorSubject,
            };
            forbidden.AddRange(_subjects);
            if (_token is not null) forbidden.Add(_token);
            foreach (var value in forbidden)
            {
                result.Text.Should().NotContain(value);
            }

            result.Text.Length.Should().BeLessThan(1024);
        }

        internal async Task<string> IssuerAsync()
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await using var command = new NpgsqlCommand("SELECT issuer FROM public.identity_authority", connection);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        internal async Task<long> RebindRowsAsync()
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
            await using var command = new NpgsqlCommand(
                "SELECT count(*) FROM public.audit_log WHERE operation LIKE 'identity.authority.rebind%'", connection);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        internal async Task<List<string>> OperationsAsync(string correlation)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
            await using var command = new NpgsqlCommand(
                "SELECT operation FROM public.audit_log WHERE correlation_id = @c ORDER BY timestamp", connection);
            command.Parameters.AddWithValue("c", correlation);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<string>();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
            return rows;
        }

        public async ValueTask DisposeAsync()
        {
            await _realm.DisposeAsync();
            await _operator.DisposeAsync();
            await _database.DisposeAsync();
        }
    }
}
