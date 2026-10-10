using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Tools.Seeder;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>One copied production build; fresh real API budgets for each bounded proof phase.</summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PublicAdmissionRenderingTests(PublicReadFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string PrivateMarker = "admission-fixture-private-provider-value";
    private static readonly string[] ValidLocales = ["en"];
    private static readonly string[] DuplicateLocales = ["en", "en"];
    private sealed record WireCase(string Name, string Kind, int ApiStatus, string? RetryAfter,
        string Method, int Status, string? ExpectedRetryAfter = null, string Code = "dependency_unavailable");
    private static readonly WireCase[] WireCases =
    [
        new("missing-get", "problem", 404, null, "GET", 404, Code: "not_found"),
        new("missing-head", "problem", 404, null, "HEAD", 404, Code: "not_found"),
        new("rate-zero", "problem", 429, "0", "GET", 429, "0", "rate_limited"),
        new("rate-head", "problem", 429, "60", "HEAD", 429, "60", "rate_limited"),
        new("unavailable-zero", "problem", 503, "0", "GET", 503, "0"),
        new("unavailable-head", "problem", 503, "60", "HEAD", 503, "60"),
        new("audit-retry", "problem", 503, "60", "GET", 503, "60", "audit_unavailable"),
        new("retry-over", "problem", 429, "61", "GET", 429, Code: "rate_limited"),
        new("retry-negative", "problem", 429, "-1", "GET", 429, Code: "rate_limited"),
        new("retry-fraction", "problem", 429, "0.5", "GET", 429, Code: "rate_limited"),
        new("retry-date", "problem", 503, "Wed, 21 Oct 2026 07:28:00 GMT", "GET", 503),
        new("retry-malformed", "problem", 503, "invalid", "GET", 503),
        new("retry-unknown", "problem", 503, "60", "GET", 503, Code: "unknown_fixture_code"),
        new("retry-other-status", "problem", 500, "60", "GET", 503),
        new("malformed-json", "malformed", 200, null, "GET", 503),
        new("malformed-rate-problem", "malformed-problem", 429, "60", "GET", 503),
        new("malformed-unavailable-problem", "malformed-problem", 503, "60", "GET", 503),
        new("wrong-content-type", "wrong-content-type", 200, null, "GET", 503),
        new("empty-body", "empty", 200, null, "GET", 503),
        new("invalid-shape", "invalid-shape", 200, null, "GET", 503),
        new("invalid-site", "invalid-site", 200, null, "GET", 503),
        new("invalid-membership", "invalid-membership", 200, null, "GET", 503),
        new("oversized", "oversized", 200, null, "GET", 503),
        new("decoded-oversized", "compressed", 200, null, "GET", 503),
        new("transport", "transport", 0, null, "GET", 503),
        new("header-deadline", "hold", 0, null, "GET", 503),
        new("body-deadline", "body-hold", 200, null, "GET", 503),
    ];

    [Fact]
    public async Task Native_admission_preserves_wire_failures_request_lifetimes_and_browser_fallback()
    {
        var secret = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
        var root = RepositoryRoot();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        ApiHost? host = null;
        using var process = RendererProcess(root);
        var started = false;
        var verified = false;
        var position = 0;
        var hmrChanged = false;
        var hmrReads = 0;
        var phases = new List<string>();
        var firstCourse = SeedData.English.Curriculum!.Courses.First(row => row.Status == "Published"
            && row.ContentAccess == "public" && row.OrganizationId is null);
        var english = firstCourse.Translations.Single();
        try
        {
            host = StartApi(secret);
            started = process.Start();
            started.Should().BeTrue();
            var errors = BoundedErrorsAsync(process.StandardError, deadline.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                apiOrigin = host.Origin,
                secret,
                privateMarker = PrivateMarker,
                locale = english.Locale,
                courseSlug = english.Slug,
                tenants = new[]
                {
                    new { host = SeedData.English.Host, name = SeedData.English.DisplayName, locale = english.Locale },
                    new { host = SeedData.Yoga.Host, name = SeedData.Yoga.DisplayName,
                        locale = SeedData.Yoga.Curriculum!.Locales.Single(row => row.IsDefault).Locale },
                },
                wireCases = WireCases.Select(row => new { row.Name, row.Method, row.Status, retryAfter = row.ExpectedRetryAfter }),
            }, JsonOptions));
            while (await process.StandardOutput.ReadLineAsync(deadline.Token) is { } checkpoint)
            {
                checkpoint.Length.Should().BeLessThan(64, "only closed control tokens leave the renderer");
                string? nextOrigin = null;
                if (checkpoint == "build-complete") host.Observation.Requests.Should().BeEmpty();
                else if (checkpoint == "hmr-before")
                {
                    host.Observation.Requests.Should().HaveCount(position);
                    hmrReads = fixture.Observation.Reads;
                }
                else if (checkpoint == "hmr-change")
                {
                    await fixture.ExecuteAsync(SeedData.English, "UPDATE tenants SET display_name=@name WHERE id=@tenant",
                        new Dictionary<string, object> { ["name"] = SeedData.English.DisplayName + " HMR refresh" });
                    hmrChanged = true;
                }
                else if (checkpoint == "hmr-after")
                {
                    AssertCalls(host, ref position, 2, 2);
                    fixture.Observation.Reads.Should().Be(hmrReads + 2);
                }
                else if (checkpoint == "hmr-restore")
                {
                    await fixture.ExecuteAsync(SeedData.English, "UPDATE tenants SET display_name=@name WHERE id=@tenant",
                        new Dictionary<string, object> { ["name"] = SeedData.English.DisplayName });
                    hmrChanged = false;
                }
                else if (checkpoint.StartsWith("phase-", StringComparison.Ordinal))
                {
                    checkpoint.Should().BeOneOf("phase-wire", "phase-lifetime", "phase-browser");
                    phases.Add(checkpoint);
                    if (checkpoint != "phase-wire")
                    {
                        VerifyPhase(host, position);
                        await host.DisposeAsync();
                        host = StartApi(secret);
                    }
                    position = 0;
                    nextOrigin = host.Origin;
                }
                else if (checkpoint.StartsWith("arm-", StringComparison.Ordinal))
                {
                    host.Observation.Requests.Length.Should().Be(position, "each earlier API call belongs to an asserted checkpoint");
                    var name = checkpoint[4..];
                    if (name == "overlap") host.Observation.ArmGate("/api/v1/public/site", 4);
                    else if (name == "shutdown") host.Observation.ArmGate("/api/v1/public/courses", 2);
                    else if (name is "bootstrap" or "content")
                        host.Observation.Arm(new WireCase(name, "hold", 0, null, "GET", 503), name == "bootstrap" ? "/api/v1/public/site" : "/api/v1/public/courses");
                    else if (name is "browser-429" or "browser-503")
                    {
                        var status = name == "browser-429" ? 429 : 503;
                        host.Observation.Arm(new WireCase(name, "problem", status, "0", "GET", status,
                            Code: status == 429 ? "rate_limited" : "dependency_unavailable"));
                    }
                    else host.Observation.Arm(WireCases.Single(row => row.Name == name));
                }
                else if (checkpoint == "release-overlap") host.Observation.ReleaseGate();
                else if (checkpoint is "held-overlap" or "held-shutdown")
                    await host.Observation.WaitForGateAsync(deadline.Token);
                else if (checkpoint.StartsWith("held-", StringComparison.Ordinal))
                    await host.Observation.WaitForHeldAsync(deadline.Token);
                else if (checkpoint.StartsWith("aborted-", StringComparison.Ordinal))
                {
                    if (checkpoint == "aborted-shutdown")
                    {
                        await host.Observation.WaitForGateAbortAsync(deadline.Token);
                        AssertCalls(host, ref position, 4, 2);
                        host.Observation.VerifyGate();
                    }
                    else
                    {
                        await host.Observation.WaitForAbortAsync(deadline.Token);
                        var expected = checkpoint == "aborted-bootstrap" ? 1 : 2;
                        AssertCalls(host, ref position, expected, 1);
                        host.Observation.VerifyFault();
                    }
                }
                else if (checkpoint.StartsWith("done-", StringComparison.Ordinal))
                {
                    var name = checkpoint[5..];
                    var wire = WireCases.SingleOrDefault(row => row.Name == name);
                    if (wire is not null)
                    {
                        AssertCalls(host, ref position, 1, 1);
                        if (wire.Kind is "hold" or "body-hold") await host.Observation.WaitForAbortAsync(deadline.Token);
                        host.Observation.VerifyFault();
                    }
                    else if (name is "browser-429" or "browser-503")
                    {
                        var calls = AssertCalls(host, ref position, 3, 2);
                        calls.Select(row => row.Status).Should().Equal(name == "browser-429" ? 429 : 503, 200, 200);
                        host.Observation.VerifyFault();
                    }
                    else if (name.StartsWith("context-", StringComparison.Ordinal) || name == "configuration")
                        AssertCalls(host, ref position, 0, 0);
                    else if (name is "html-overlap" or "rsc-overlap" or "prefetch-overlap" or "mixed-overlap")
                    {
                        AssertCalls(host, ref position, 8, 4);
                        host.Observation.VerifyGate();
                    }
                    else if (name == "keep-alive") AssertCalls(host, ref position, 4, 2);
                    else if (name == "late-head" || name == "browser-initial" || name == "entry-refusal")
                        AssertCalls(host, ref position, 1, 1);
                    else if (name is "healthy-wire" or "healthy-lifetime" or "head" or "hmr-before" or "hmr-after")
                        AssertCalls(host, ref position, 2, 1);
                    else throw new InvalidOperationException("Unexpected admission proof checkpoint.");
                }
                else if (checkpoint == "verified")
                {
                    VerifyPhase(host, position);
                    verified = true;
                }
                else throw new InvalidOperationException("Unexpected admission proof checkpoint.");
                await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { kind = "continue", apiOrigin = nextOrigin }, JsonOptions));
            }
            await process.WaitForExitAsync(deadline.Token);
            process.ExitCode.Should().Be(0, await errors);
            verified.Should().BeTrue();
            phases.Should().Equal("phase-wire", "phase-lifetime", "phase-browser");
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.StandardInput.Close();
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                try { await process.WaitForExitAsync(cleanup.Token); }
                catch (OperationCanceledException)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            if (hmrChanged)
                await fixture.ExecuteAsync(SeedData.English, "UPDATE tenants SET display_name=@name WHERE id=@tenant",
                    new Dictionary<string, object> { ["name"] = SeedData.English.DisplayName });
            if (host is not null) await host.DisposeAsync();
        }
    }

    private ApiHost StartApi(string secret)
    {
        var observation = new AdmissionObservation(secret);
        var factory = fixture.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Tenancy:TrustedHop:Networks:0", "127.0.0.1/32");
            builder.UseSetting("Tenancy:TrustedHop:Secrets:0", secret);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IStartupFilter>(observation);
                services.AddSingleton<ILogEventSink>(observation);
            });
        });
        factory.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var address = factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new(factory, client, address, observation, fixture.Observation.Reads);
    }

    private void VerifyPhase(ApiHost host, int position)
    {
        host.Observation.Requests.Should().HaveCount(position);
        host.Observation.Requests.Should().HaveCountLessThanOrEqualTo(60, "each phase uses the unchanged single-visitor budget");
        host.Observation.Requests.Should().OnlyContain(row => row.ValidHop);
        host.Observation.PrivateLog.Should().BeFalse();
        host.Observation.Logs.Should().BeGreaterThan(0, "API containment observes nonempty real structured logs");
        fixture.Observation.Reads.Should().BeGreaterThan(host.InitialReads, "positive controls execute the production reader as READ ONLY learnstack_app");
    }

    private static ObservedRequest[] AssertCalls(ApiHost host, ref int position, int count, int sites)
    {
        var rows = host.Observation.Requests.Skip(position).ToArray();
        rows.Should().HaveCount(count);
        rows.Count(row => row.Path == "/api/v1/public/site").Should().Be(sites);
        rows.Count(row => row.Path.StartsWith("/api/v1/public/courses", StringComparison.Ordinal)).Should().Be(count - sites);
        if (count > 0) rows.Should().OnlyContain(row => row.ValidHop);
        position += rows.Length;
        return rows;
    }

    private sealed record ApiHost(WebApplicationFactory<Program> Factory, HttpClient Client, string Origin,
        AdmissionObservation Observation, int InitialReads) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private sealed class ObservedRequest(string path, bool validHop)
    {
        public string Path { get; } = path;
        public bool ValidHop { get; } = validHop;
        public int Status { get; set; }
    }

    /// <summary>Explicit one-shot wire faults; healthy operations keep the real API pipeline.</summary>
    private sealed class AdmissionObservation(string secret) : IStartupFilter, ILogEventSink
    {
        private readonly ConcurrentQueue<ObservedRequest> _requests = new();
        private Fault? _fault;
        private Fault? _armed;
        private Gate? _gate;
        private int _privateLog;
        private int _logs;
        public ObservedRequest[] Requests => _requests.ToArray();
        public bool PrivateLog => Volatile.Read(ref _privateLog) != 0;
        public int Logs => Volatile.Read(ref _logs);
        public void ArmGate(string path, int count)
        {
            _gate.Should().BeNull();
            _gate = new(path, count);
        }
        public Task WaitForGateAsync(CancellationToken token) => _gate!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        public Task WaitForGateAbortAsync(CancellationToken token) => _gate!.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        public void ReleaseGate() => _gate!.Release.TrySetResult();
        public void VerifyGate()
        {
            _gate!.Arrivals.Should().Be(_gate.Count, "identical requests count separately in the overlap multiset");
            _gate.Entered.Task.IsCompletedSuccessfully.Should().BeTrue();
            _gate = null;
        }
        public void Arm(WireCase value, string path = "/api/v1/public/site")
        {
            _armed.Should().BeNull();
            _armed = new(value, path);
            Volatile.Write(ref _fault, _armed);
        }
        public Task WaitForHeldAsync(CancellationToken token) => _armed!.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        public Task WaitForAbortAsync(CancellationToken token) => _armed!.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        public void VerifyFault()
        {
            _fault.Should().BeNull("the exact one-shot API fault was consumed");
            _armed!.Entered.Task.IsCompletedSuccessfully.Should().BeTrue();
            _armed = null;
        }
        public void Emit(LogEvent logEvent)
        {
            Interlocked.Increment(ref _logs);
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            new Serilog.Formatting.Compact.RenderedCompactJsonFormatter().Format(logEvent, output);
            var value = output.ToString();
            if (value.Contains(secret, StringComparison.Ordinal) || value.Contains(PrivateMarker, StringComparison.Ordinal)
                || value.Contains("x-learnstack-", StringComparison.OrdinalIgnoreCase)
                || Regex.IsMatch(value, @"(?<![A-Za-z0-9_.-])v1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{43}(?![A-Za-z0-9_.=-])", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                Interlocked.Exchange(ref _privateLog, 1);
        }
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (!context.Request.Path.StartsWithSegments("/api/v1/public")) { await continuation(); return; }
                var headers = context.Request.Headers;
                var validHop = headers["X-LearnStack-Hop-Secret"].Count == 1
                    && headers["X-LearnStack-Hop-Secret"] == secret
                    && headers.Accept == "application/json"
                    && headers["traceparent"].Count == 1
                    && Regex.IsMatch(headers["traceparent"].ToString(), "^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))
                    && headers["X-LearnStack-Visitor-Address"] == "127.0.0.1"
                    && headers["X-LearnStack-Host"].Count == 1
                    && (headers["X-LearnStack-Host"] == SeedData.English.Host + ":3000" || headers["X-LearnStack-Host"] == SeedData.Yoga.Host + ":3000")
                    && !headers.ContainsKey("X-LearnStack-Ingress-Provenance")
                    && !headers.ContainsKey("Authorization") && !headers.ContainsKey("Cookie")
                    && !headers.ContainsKey("X-Tenant-Id") && !headers.ContainsKey("X-Organization-Id")
                    && !headers.ContainsKey("X-Locale") && !headers.ContainsKey("X-Forwarded-For");
                var row = new ObservedRequest(context.Request.Path.Value!, validHop);
                _requests.Enqueue(row);
                if (Volatile.Read(ref _gate) is { } gate && context.Request.Path == gate.Path)
                {
                    try { await gate.ArriveAsync(context.RequestAborted); }
                    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { return; }
                }
                var fault = Volatile.Read(ref _fault);
                if (fault is not null && context.Request.Path == fault.Path
                    && Interlocked.CompareExchange(ref _fault, null, fault) == fault)
                {
                    fault.Entered.TrySetResult();
                    try { await RespondAsync(context, fault.Value); }
                    finally
                    {
                        if (context.RequestAborted.IsCancellationRequested) fault.Aborted.TrySetResult();
                    }
                }
                else await continuation();
                row.Status = context.Response.StatusCode;
            });
            next(app);
        };
        private static async Task RespondAsync(HttpContext context, WireCase fault)
        {
            if (fault.Kind == "transport") { context.Abort(); return; }
            if (fault.Kind is "hold" or "body-hold")
            {
                if (fault.Kind == "body-hold")
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"displayName\":", context.RequestAborted);
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
                try { await Task.Delay(Timeout.InfiniteTimeSpan, context.RequestAborted); }
                catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
                return;
            }
            context.Response.StatusCode = fault.ApiStatus;
            context.Response.ContentType = fault.Kind is "problem" or "malformed-problem" ? "application/problem+json"
                : fault.Kind == "wrong-content-type" ? "text/plain" : "application/json";
            context.Response.Headers.CacheControl = "no-store";
            if (fault.RetryAfter is not null) context.Response.Headers.RetryAfter = fault.RetryAfter;
            var body = fault.Kind switch
            {
                "problem" => JsonSerializer.Serialize(new
                {
                    type = "https://fixture.invalid/private",
                    title = PrivateMarker,
                    status = fault.ApiStatus,
                    detail = PrivateMarker,
                    code = fault.Code,
                    messageKey = "lockey_fixture_private",
                    correlationId = "fixture-private-correlation",
                    instance = "/fixture-private"
                }, JsonOptions),
                "malformed" => "{\"private\":",
                "malformed-problem" => "{\"status\":\"invalid\",\"detail\":\"" + PrivateMarker + "\"}",
                "wrong-content-type" => JsonSerializer.Serialize(new
                {
                    displayName = PrivateMarker,
                    enabledLocales = ValidLocales,
                    defaultLocale = "en",
                    theme = (object?)null,
                    showPlatformAttribution = true
                }, JsonOptions),
                "empty" => "",
                "invalid-shape" => "[]",
                "invalid-site" => "{\"displayName\":42}",
                "invalid-membership" => JsonSerializer.Serialize(new
                {
                    displayName = PrivateMarker,
                    enabledLocales = DuplicateLocales,
                    defaultLocale = "en",
                    theme = (object?)null,
                    showPlatformAttribution = true
                }, JsonOptions),
                "oversized" or "compressed" => "{\"displayName\":\"" + new string('x', 8 * 1024 * 1024) + "\"}",
                _ => throw new InvalidOperationException("Unknown admission fixture response."),
            };
            if (fault.Kind == "compressed")
            {
                context.Response.Headers.ContentEncoding = "gzip";
                await using var gzip = new GZipStream(context.Response.Body, CompressionLevel.Fastest, leaveOpen: true);
                await gzip.WriteAsync(System.Text.Encoding.UTF8.GetBytes(body), context.RequestAborted);
            }
            else await context.Response.WriteAsync(body, context.RequestAborted);
        }
        private sealed record Fault(WireCase Value, string Path)
        {
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        private sealed class Gate(string path, int count)
        {
            private int _arrivals;
            private int _aborted;
            public string Path { get; } = path;
            public int Count { get; } = count;
            public int Arrivals => Volatile.Read(ref _arrivals);
            public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public async Task ArriveAsync(CancellationToken token)
            {
                if (Interlocked.Increment(ref _arrivals) == Count) Entered.TrySetResult();
                try { await Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    if (Interlocked.Increment(ref _aborted) == Count) Aborted.TrySetResult();
                    throw;
                }
            }
        }
    }

    private static Process RendererProcess(string root)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root, "frontend/apps/web/scripts/verify-public-admission.mjs"));
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        start.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "1";
        return new() { StartInfo = start };
    }
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "frontend/pnpm-lock.yaml"))) return directory.FullName;
        throw new InvalidOperationException("Cannot find admission rendering fixture source.");
    }
    private static async Task<string> BoundedErrorsAsync(StreamReader stream, CancellationToken token)
    {
        var output = new System.Text.StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
            if (output.Length + read <= 4096) output.Append(buffer, 0, read);
        var text = output.ToString();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).All(line => line.StartsWith("Public admission fixture ", StringComparison.Ordinal))
            ? text : "Public admission fixture exited unexpectedly.";
    }
}
