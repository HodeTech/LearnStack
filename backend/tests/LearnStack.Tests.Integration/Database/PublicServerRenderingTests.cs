using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Production Next/native TLS/SDK/API/PostgreSQL proof. The disposable renderer
/// owns its route; no diagnostic page is added to the shipped application.
/// </summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PublicServerRenderingTests(PublicReadFixture fixture)
{
    private const string SuppliedTrace = "00-1234567890abcdef1234567890abcdef-1234567890abcdef-01";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Production_rendering_is_host_isolated_fresh_and_private_through_the_real_public_API()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var observed = new RenderingObservation(secret);
        await using var factory = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tenancy:TrustedHop:Networks:0", "127.0.0.1/32");
            builder.UseSetting("Tenancy:TrustedHop:Secrets:0", secret);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(observed);
                // Production Serilog reads sinks from DI; keep its real
                // configuration/enrichers and observe the same emitted events.
                services.AddSingleton<ILogEventSink>(observed);
            });
        });
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var addresses = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        addresses.Should().NotBeNull();
        var origin = addresses!.Addresses.Single();

        // The shared exact-locale slug and all identities/expected content come
        // from the seeder's sole declaration, not a second JS seed registry.
        var firstCourse = SeedData.English.Curriculum!.Courses.First(course =>
            course.Status == "Published" && course.ContentAccess == "public" && course.OrganizationId is null);
        var firstTranslation = firstCourse.Translations.Single();
        var locale = firstTranslation.Locale;
        var sharedSlug = firstTranslation.Slug;
        var secondCourse = SeedData.Yoga.Curriculum!.Courses.Single(course =>
            course.Translations.Any(translation => translation.Locale == locale && translation.Slug == sharedSlug));
        var tenants = new[]
        {
            TenantInput(SeedData.English, firstCourse, locale),
            TenantInput(SeedData.Yoga, secondCourse, locale)
        };
        var root = RepositoryRoot();
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(root, "frontend/apps/web/scripts/verify-public-rendering.mjs"));
        // Neither workstation secrets nor Node loader/TLS overrides enter the
        // child. Configuration travels once on stdin, never argv or test output.
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        using var process = new Process { StartInfo = start };
        process.Start().Should().BeTrue();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var errors = BoundedErrorsAsync(process.StandardError, deadline.Token);
        var draft = false;
        var verified = false;
        var tracePosition = 0;
        var beforeProtocol = -1;
        var beforeStock = -1;
        var beforeStockReads = -1;
        var checkpoints = new List<string>();
        try
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                apiOrigin = origin,
                secret,
                locale,
                courseSlug = sharedSlug,
                traceparent = SuppliedTrace,
                tenants
            }, JsonOptions));
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } checkpoint)
            {
                checkpoint.Length.Should().BeLessThan(40, "only bounded control tokens leave the fixture");
                checkpoints.Add(checkpoint);
                switch (checkpoint)
                {
                    case "build-complete":
                        observed.Requests.Should().BeEmpty("production compilation must never fetch tenant data");
                        break;
                    case "trace-supplied":
                    case "trace-missing":
                    case "trace-malformed":
                        var requests = observed.Requests.Skip(tracePosition).ToArray();
                        requests.Should().HaveCount(3, "middleware bootstrap, page bootstrap and content all use the real API");
                        requests.Take(2).Should().OnlyContain(request => request.Path == "/api/v1/public/site");
                        requests[2].Path.Should().StartWith("/api/v1/public/courses");
                        requests.Select(request => request.TraceId).Distinct().Should().ContainSingle("one incoming render has one distributed trace");
                        requests.Should().OnlyContain(request => request.TraceId.Length == 32 && request.TraceId != new string('0', 32));
                        if (checkpoint == "trace-supplied") requests[0].TraceId.Should().Be(SuppliedTrace.Substring(3, 32));
                        else requests[0].TraceId.Should().NotBe(SuppliedTrace.Substring(3, 32), "missing/malformed contexts get a fresh trace");
                        tracePosition += requests.Length;
                        break;
                    case "protocol-before":
                        beforeProtocol = observed.Requests.Length;
                        break;
                    case "protocol-after":
                        observed.Requests.Skip(beforeProtocol).Should().HaveCount(6,
                            "five protocol GET probes and HEAD each run the real middleware bootstrap once");
                        observed.Requests.Skip(beforeProtocol).Should().OnlyContain(request => request.Path == "/api/v1/public/site",
                            "the test-owned protocol route makes no page/content API calls");
                        break;
                    case "stock-before":
                        beforeStock = observed.Requests.Length;
                        beforeStockReads = fixture.Observation.Reads;
                        beforeStock.Should().BeGreaterThan(0, "the same API already served native positive controls");
                        break;
                    case "stock-after":
                        observed.Requests.Length.Should().Be(beforeStock, "stock Next forgery fails before any API call");
                        fixture.Observation.Reads.Should().Be(beforeStockReads, "stock forgery cannot bootstrap or read PostgreSQL");
                        break;
                    case "make-draft":
                        await SetStatusAsync(firstCourse, "Draft");
                        draft = true;
                        break;
                    case "restore-published":
                        await SetStatusAsync(firstCourse, firstCourse.Status);
                        draft = false;
                        break;
                    case "verified":
                        verified = true;
                        break;
                    default:
                        Assert.Fail("Unexpected production-rendering control token.");
                        break;
                }
                await process.StandardInput.WriteLineAsync("continue");
            }
            await process.WaitForExitAsync(deadline.Token);
            var errorOutput = await errors;
            process.ExitCode.Should().Be(0, errorOutput);
            verified.Should().BeTrue("the production fixture must execute every scenario");
            checkpoints.Should().Equal("build-complete", "trace-supplied", "trace-missing", "trace-malformed", "protocol-before", "protocol-after", "stock-before", "stock-after", "make-draft", "restore-published", "verified");
            observed.Requests.Length.Should().BeInRange(1, 59, "the fixture stays within one real anonymous visitor budget");
            observed.Requests.Should().OnlyContain(request => request.ValidHop, "the real caller uses the closed authenticated hop");
            observed.Logs.Should().BeGreaterThan(0, "API log containment needs a nonempty real logging subject");
            observed.PrivateLog.Should().BeFalse("private carriers, provenance and the hop credential cannot enter API logs");
        }
        finally
        {
            // Closing the control pipe asks the Node fixture to terminate its
            // own process groups and delete TLS/build files, before a last resort kill.
            try
            {
                process.StandardInput.Close();
                if (!process.HasExited)
                {
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await process.WaitForExitAsync(shutdown.Token); }
                    catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                }
            }
            finally { if (draft) await SetStatusAsync(firstCourse, firstCourse.Status); }
        }
    }

    private Task SetStatusAsync(SeedCourse course, string status) => fixture.ExecuteOwnerFixtureAsync(
        SeedData.English,
        "UPDATE courses SET status=@status WHERE tenant_id=@tenant AND id=@id",
        new Dictionary<string, object> { ["id"] = course.Id, ["status"] = status.ToLowerInvariant() });

    private static object TenantInput(SeedTenant tenant, SeedCourse course, string locale)
    {
        var lesson = course.Lessons.First(row => row.Status == "Published");
        var translation = lesson.Translations.Single(row => row.Locale == locale);
        using var body = JsonDocument.Parse(translation.Body);
        return new
        {
            host = tenant.Host,
            name = tenant.DisplayName,
            courseTitle = course.Translations.Single(row => row.Locale == locale).Title,
            lessonSlug = translation.Slug,
            lessonTitle = translation.Title,
            lessonText = body.RootElement.EnumerateObject().First().Value.GetString(),
            defaultLocale = tenant.Curriculum!.Locales.Single(row => row.IsDefault).Locale
        };
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend/pnpm-lock.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Cannot find production-rendering fixture source.");
    }

    private static async Task<string> BoundedErrorsAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            // Node emits only finite test-owned stages; never forward arbitrary
            // provider/compiler diagnostics into test results, even on a crash.
            if (result.Length + read <= 4096) result.Append(buffer, 0, read);
        }
        return result.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .All(line => line.StartsWith("Production rendering fixture ", StringComparison.Ordinal))
            ? result.ToString() : "Production rendering fixture exited unexpectedly.";
    }

    private sealed record ObservedRequest(string Path, string TraceId, bool ValidHop);

    /// <summary>Observes the real Kestrel request without changing API resolution or transactions.</summary>
    private sealed class RenderingObservation(string secret) : IStartupFilter, ILogEventSink
    {
        private readonly ConcurrentQueue<ObservedRequest> _requests = new();
        private int _logs;
        private int _privateLog;
        public ObservedRequest[] Requests => _requests.ToArray();
        public int Logs => Volatile.Read(ref _logs);
        public bool PrivateLog => Volatile.Read(ref _privateLog) != 0;

        public void Emit(LogEvent logEvent)
        {
            using var text = new StringWriter(CultureInfo.InvariantCulture);
            new Serilog.Formatting.Compact.RenderedCompactJsonFormatter().Format(logEvent, text);
            var value = text.ToString();
            // Retain only counters/verdicts, never sensitive event values.
            Interlocked.Increment(ref _logs);
            if (value.Contains(secret, StringComparison.Ordinal)
                || value.Contains("x-learnstack-", StringComparison.OrdinalIgnoreCase)
                || value.Contains("v1.", StringComparison.Ordinal))
                Interlocked.Exchange(ref _privateLog, 1);
        }

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path.StartsWithSegments("/api/v1/public"))
                {
                    var headers = context.Request.Headers;
                    var validHop = headers["X-LearnStack-Hop-Secret"].Count == 1
                        && headers["X-LearnStack-Hop-Secret"] == secret
                        && headers["X-LearnStack-Visitor-Address"] == "127.0.0.1"
                        && headers["X-LearnStack-Host"].Count == 1
                        && !headers.ContainsKey("X-LearnStack-Ingress-Provenance")
                        && !headers.ContainsKey("Authorization") && !headers.ContainsKey("Cookie")
                        && !headers.ContainsKey("X-Tenant-Id") && !headers.ContainsKey("X-Organization-Id")
                        && !headers.ContainsKey("X-Locale") && !headers.ContainsKey("X-Forwarded-For");
                    _requests.Enqueue(new(context.Request.Path.Value!, Activity.Current?.TraceId.ToHexString() ?? "", validHop));
                }
                await continuation();
            });
            next(app);
        };
    }
}
