using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class GlobalIdentityAuthorityToolTests(SchemaFixture schema)
{
    private const string Authority = "019a1929-5450-7bbb-8df1-516e8e002501";
    private const string Issuer = "https://installer-proof.invalid/realms/learnstack";

    [Fact]
    public async Task Deployment_child_installs_once_verifies_repeat_and_refuses_rebind_or_app_credentials_without_values()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        (await RunAsync(database.MigrationConnectionString, Issuer)).Code.Should().Be(0);
        (await RunAsync(database.MigrationConnectionString, Issuer)).Code.Should().Be(0);
        var mismatch = await RunAsync(database.MigrationConnectionString,
            "https://private-issuer-canary.invalid/realms/learnstack");
        mismatch.Code.Should().Be(2);
        mismatch.Text.Should().NotContain("private-issuer-canary");
        var app = await RunAsync(database.AppConnectionString, Issuer);
        app.Code.Should().Be(2);
        foreach (var result in new[] { mismatch, app })
        {
            result.Text.Should().NotContain(database.MigrationConnectionString).And.NotContain(database.AppConnectionString);
            result.Text.Should().NotContain(Authority).And.NotContain(Issuer);
            result.Text.Length.Should().BeLessThan(1024);
        }
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var read = new NpgsqlCommand("SELECT authority_id::text, issuer FROM public.identity_authority WHERE id = 1", connection);
        await using var reader = await read.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be(Authority);
        reader.GetString(1).Should().Be(Issuer);
        (await reader.ReadAsync()).Should().BeFalse();
    }

    private static async Task<(int Code, string Text)> RunAsync(string connection, string issuer)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.runtimeconfig.json"));
        start.ArgumentList.Add("--depsfile");
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "LearnStack.Tests.Integration.deps.json"));
        start.ArgumentList.Add(Assembly.Load("LearnStack.Tools.IdentityAuthorityInstaller").Location);
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "SystemRoot", "DOTNET_ROOT", "HOME" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["ConnectionStrings__Migration"] = connection;
        start.Environment["Identity__AuthorityId"] = Authority;
        start.Environment["Authentication__Tenant__Issuer"] = issuer;
        using var child = new Process { StartInfo = start };
        child.Start().Should().BeTrue();
        var output = child.StandardOutput.ReadToEndAsync();
        var errors = child.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await child.WaitForExitAsync(deadline.Token); }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(CancellationToken.None);
            }
        }
        return (child.ExitCode, await output + await errors);
    }
}
