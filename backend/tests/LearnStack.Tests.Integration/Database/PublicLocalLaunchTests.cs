using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Exercises the actual local API launcher with explicit application and dispatcher credentials.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PublicLocalLaunchTests(PublicReadFixture parent)
{
    private const string Secret = "test-only-public-local-launch-secret-at-least-32";

    [Fact]
    public async Task Public_local_API_launcher_serves_seeded_reads_without_a_PlatformAdmin_credential()
        => await AssertLauncherAsync(useDemoRuntime: false);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Demo_API_runtime_rejects_a_foreign_user_secret_only_with_profile_isolation(bool isolateProfile)
        => await AssertLauncherAsync(useDemoRuntime: true, isolateProfile);

    private async Task AssertLauncherAsync(bool useDemoRuntime, bool isolateProfile = true)
    {
        await using var fixture = await IdentityPublicReadFixture.CreateAsync(parent, "http://localhost:8080/realms/learnstack");
        var repository = RepositoryRoot();
        var temporary = Path.Combine(Path.GetTempPath(), "learnstack-public-local-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(temporary, "frontend/apps/web");
        var backend = Path.Combine(temporary, "backend");
        var foreignSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        using var process = new Process();
        var started = false;
        Task output = Task.CompletedTask;
        Task errors = Task.CompletedTask;
        try
        {
            Directory.CreateDirectory(Path.Combine(temporary, "scripts"));
            Directory.CreateDirectory(Path.Combine(app, "src/server/auth"));
            Directory.CreateDirectory(Path.Combine(temporary, "user-secrets"));
            Directory.CreateDirectory(Path.Combine(temporary, "owned-profile"));
            File.WriteAllText(Path.Combine(temporary, "package.json"), "{\"type\":\"module\"}");
            File.Copy(Path.Combine(repository, "scripts/public-local.mjs"), Path.Combine(temporary, "scripts/public-local.mjs"));
            File.Copy(Path.Combine(repository, "scripts/public-runtime.mjs"), Path.Combine(temporary, "scripts/public-runtime.mjs"));
            File.Copy(Path.Combine(repository, "scripts/demo-database.mjs"), Path.Combine(temporary, "scripts/demo-database.mjs"));
            if (!isolateProfile)
            {
                // Deliberately remove only the profile boundary in the owned copy.
                // The same live request must now reach the site as an accepted hop.
                var runtime = Path.Combine(temporary, "scripts/public-runtime.mjs");
                var source = File.ReadAllText(runtime);
                foreach (var assignment in new[] { "HOME: profile,", "APPDATA: profile,", "TMPDIR: profile," })
                {
                    source.Should().Contain(assignment, "the mutation must change the shipped profile boundary");
                    source = source.Replace(assignment, string.Empty, StringComparison.Ordinal);
                }
                File.WriteAllText(runtime, source);
            }
            foreach (var file in new[] { "tsconfig.json", "tsconfig.server.json", "src/server/ingress.ts", "src/server/auth/http-paths.ts" })
                File.Copy(Path.Combine(repository, "frontend/apps/web", file), Path.Combine(app, file));
            Directory.CreateSymbolicLink(Path.Combine(app, "node_modules"), Path.Combine(repository, "frontend/apps/web/node_modules"));
            Directory.CreateSymbolicLink(backend, Path.Combine(repository, "backend"));
            File.Exists(Path.Combine(temporary, ".env")).Should().BeFalse();
            File.Exists(Path.Combine(app, ".env.local")).Should().BeFalse();

            // Compile the current ingress source with its real project options;
            // never depend on a preexisting ignored .server build artifact.
            await CompileIngressAsync(repository, app, deadline.Token);
            process.StartInfo = NodeStart(temporary);
            if (useDemoRuntime)
            {
                // Exercise the shipped demo environment factory with a real API,
                // while the launcher itself sees a planted foreign secret profile.
                var secrets = Path.Combine(temporary, "user-secrets", "Microsoft", "UserSecrets", "learnstack-api-dev");
                Directory.CreateDirectory(secrets);
                await File.WriteAllTextAsync(Path.Combine(secrets, "secrets.json"), JsonSerializer.Serialize(new
                {
                    Tenancy = new { TrustedHop = new { Secrets = new Dictionary<string, string> { ["1"] = foreignSecret } } }
                }), deadline.Token);
                var script = Path.Combine(temporary, "scripts/demo-api-probe.mjs");
                await File.WriteAllTextAsync(script, """
                    import {spawn} from 'node:child_process';
                    import {resolve} from 'node:path';
                    import {publicDemoApiEnvironment} from './public-runtime.mjs';
                    const root = resolve(import.meta.dirname, '..');
                    const env = publicDemoApiEnvironment(process.env, {
                      apiOrigin: process.env.LEARNSTACK_PUBLIC_API_ORIGIN,
                      secret: process.env.LEARNSTACK_PUBLIC_HOP_SECRET
                    }, resolve(root, 'owned-profile'));
                    const child = spawn('dotnet', [resolve(root, 'backend/src/LearnStack.Api/bin/Release/net10.0/LearnStack.Api.dll')], {
                      cwd: resolve(root, 'backend/src/LearnStack.Api'), env, stdio: 'inherit'
                    });
                    child.on('error', () => process.exit(1));
                    child.on('exit', code => process.exit(code ?? 1));
                    """, deadline.Token);
                process.StartInfo.ArgumentList.Add(script);
            }
            else
            {
                process.StartInfo.ArgumentList.Add(Path.Combine(temporary, "scripts/public-local.mjs"));
                process.StartInfo.ArgumentList.Add("api");
            }
            process.StartInfo.Environment["Identity__AuthorityId"] = fixture.Registration.AuthorityId.Value.ToString("D");
            process.StartInfo.Environment["Authentication__Tenant__Issuer"] = fixture.Registration.Issuer;
            process.StartInfo.Environment["ConnectionStrings__Default"] = fixture.AppConnectionString;
            process.StartInfo.Environment["ConnectionStrings__OutboxDispatcher"] = fixture.OutboxConnectionString;
            process.StartInfo.Environment["LEARNSTACK_PUBLIC_API_ORIGIN"] = "http://127.0.0.1:0";
            process.StartInfo.Environment["LEARNSTACK_PUBLIC_HOP_SECRET"] = Secret;
            // API configuration validates paths but does not open renderer TLS files.
            process.StartInfo.Environment["LEARNSTACK_PUBLIC_TLS_CERT"] = "unused-public.pem";
            process.StartInfo.Environment["LEARNSTACK_PUBLIC_TLS_KEY"] = "unused-public-key.pem";
            // The older launcher gets an empty test profile. The demo case sees
            // a populated foreign profile and must replace it with its owned one.
            process.StartInfo.Environment["APPDATA"] = Path.Combine(temporary, "user-secrets");
            process.StartInfo.Environment.ContainsKey("ConnectionStrings__PlatformAdmin").Should().BeFalse();

            var ready = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            started = process.Start();
            started.Should().BeTrue();
            output = ObserveListeningAddressAsync(process.StandardOutput, ready);
            errors = DrainAsync(process.StandardError);
            var origin = await ready.Task.WaitAsync(deadline.Token);
            origin.Port.Should().NotBe(3000).And.NotBe(3011);
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false }) { BaseAddress = origin };
            using var health = await client.GetAsync("/healthz", deadline.Token);
            health.StatusCode.Should().Be(HttpStatusCode.OK);

            foreach (var tenant in new[] { SeedData.English, SeedData.Yoga })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/public/site");
                request.Headers.Host = "gateway.internal";
                request.Headers.Add(TrustedHopOptions.HostHeaderName, tenant.Host);
                request.Headers.Add(TrustedHopOptions.SecretHeaderName, Secret);
                request.Headers.Add(AnonymousRequestIdentity.VisitorHeaderName, "203.0.113.90");
                using var response = await client.SendAsync(request, deadline.Token);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Headers.CacheControl?.NoStore.Should().BeTrue();
                var site = await response.Content.ReadFromJsonAsync<JsonElement>(deadline.Token);
                site.GetProperty("displayName").GetString().Should().Be(tenant.DisplayName);
            }
            if (useDemoRuntime)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/public/site");
                request.Headers.Host = "gateway.internal";
                request.Headers.Add(TrustedHopOptions.HostHeaderName, SeedData.English.Host);
                request.Headers.Add(TrustedHopOptions.SecretHeaderName, foreignSecret);
                request.Headers.Add(AnonymousRequestIdentity.VisitorHeaderName, "203.0.113.90");
                using var response = await client.SendAsync(request, deadline.Token);
                response.StatusCode.Should().Be(isolateProfile ? HttpStatusCode.NotFound : HttpStatusCode.OK,
                    "only the owned profile prevents a foreign secret from extending the trusted hop");
                File.Exists(Path.Combine(temporary, "user-secrets", "Microsoft", "UserSecrets",
                    "learnstack-api-dev", "secrets.json")).Should().BeTrue("the foreign profile is never modified");
            }
        }
        finally
        {
            // The actual launcher owns dotnet run and its application descendant.
            // Join the owned tree before removing its temporary configuration root.
            if (started)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(shutdown.Token);
                await Task.WhenAll(output, errors).WaitAsync(shutdown.Token);
            }
            if (Directory.Exists(backend)) Directory.Delete(backend);
            if (Directory.Exists(temporary)) Directory.Delete(temporary, recursive: true);
        }
    }

    private static async Task CompileIngressAsync(string repository, string app, CancellationToken cancellationToken)
    {
        using var compiler = new Process { StartInfo = NodeStart(app) };
        compiler.StartInfo.ArgumentList.Add(Path.Combine(repository, "frontend/apps/web/node_modules/typescript/bin/tsc"));
        compiler.StartInfo.ArgumentList.Add("--project");
        compiler.StartInfo.ArgumentList.Add("tsconfig.server.json");
        var started = false;
        Task output = Task.CompletedTask;
        Task errors = Task.CompletedTask;
        try
        {
            started = compiler.Start();
            started.Should().BeTrue();
            output = DrainAsync(compiler.StandardOutput);
            errors = DrainAsync(compiler.StandardError);
            await compiler.WaitForExitAsync(cancellationToken);
            compiler.ExitCode.Should().Be(0, "the staged current ingress source must compile before launching");
        }
        finally
        {
            if (started)
            {
                if (!compiler.HasExited) compiler.Kill(entireProcessTree: true);
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await compiler.WaitForExitAsync(shutdown.Token);
                await Task.WhenAll(output, errors).WaitAsync(shutdown.Token);
            }
        }
    }

    private static ProcessStartInfo NodeStart(string directory)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "HOME", "DOTNET_ROOT", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        return start;
    }

    private static async Task ObserveListeningAddressAsync(StreamReader stream, TaskCompletionSource<Uri> ready)
    {
        while (await stream.ReadLineAsync() is { } line)
        {
            // Retain only the owned listener address, never provider/build logs.
            // The compact JSON sink may quote/escape the rendered address.
            if (!line.Contains("Now listening on:", StringComparison.Ordinal)) continue;
            var match = Regex.Match(line, @"http://127\.0\.0\.1:\d+",
                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (match.Success) ready.TrySetResult(new Uri(match.Value));
        }
        ready.TrySetException(new InvalidOperationException("The owned local API exited before announcing readiness."));
    }

    private static async Task DrainAsync(StreamReader stream)
    {
        var buffer = new char[1024];
        while (await stream.ReadAsync(buffer) > 0) { }
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "scripts/public-local.mjs"))) return directory.FullName;
        throw new InvalidOperationException("Cannot find local API launcher source.");
    }
}
