using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// Production Next/native TLS/SDK/API/PostgreSQL proof. The disposable renderer
/// separates the P5 transport probe from unchanged P6 product routes.
/// Pagination inventory and presentation revisions belong to their own modes.
/// </summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PublicServerRenderingTests(PublicReadFixture fixture)
{
    private const string SuppliedTrace = "00-1234567890abcdef1234567890abcdef-1234567890abcdef-01";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Guid PresentationRevisionId = Guid.Parse("01930000-0000-7000-8000-000000006003");
    private static readonly PresentationField[] PresentationFields =
    [
        new("fixture_script", new("Script text", "en"), "<script>globalThis.fixturePresentationExecuted=true</script>"),
        new("fixture_markup", new("İşaretleme metni", "tr-TR"), "<img src=\"https://fixture-presentation.invalid/image\" onerror=\"alert('fixture')\"><strong>Authored markup</strong>"),
        new("fixture_javascript", new("نص البرمجية", "ar"), "javascript:alert('fixture-presentation')"),
        new("fixture_data", new("Data address", "en"), "data:text/html,<script>alert('fixture-presentation')</script>"),
        new("fixture_http", new("HTTP address", "en"), "http://fixture-presentation.invalid/content")
    ];
    private static readonly PresentationLabel PresentationTypeLabel = new("Deneme kartı", "tr-TR");

    [Theory]
    [InlineData("transport")]
    [InlineData("foundation")]
    [InlineData("foundation-pagination")]
    [InlineData("presentation")]
    public async Task Production_rendering_is_host_isolated_fresh_and_private_through_the_real_public_API(string mode)
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
        using var process = RendererProcess(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(8));
        var started = false;
        var draft = false;
        var verified = false;
        var foundationPosition = 0;
        var foundationLocales = false;
        var paginationRows = false;
        var presentationRevision = false;
        var tracePosition = 0;
        var beforeProtocol = -1;
        var beforeStock = -1;
        var beforeStockReads = -1;
        var checkpoints = new List<string>();
        try
        {
            if (mode == "foundation")
            {
                // Test-owned membership extends only this scenario. Product reads
                // still execute as learnstack_app through the real read-only API.
                await fixture.ExecuteAsync(SeedData.English,
                    "INSERT INTO tenant_locales(tenant_id,locale,is_default,is_enabled,sort) VALUES(@tenant,'ar',false,true,8),(@tenant,'tr-TR',false,true,9),(@tenant,'tr',false,true,10)");
                foundationLocales = true;
            }
            if (mode == "foundation-pagination")
            {
                await AddPaginationRowsAsync(firstCourse);
                paginationRows = true;
            }
            started = process.Start();
            started.Should().BeTrue();
            var errors = BoundedErrorsAsync(process.StandardError, deadline.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                mode,
                apiOrigin = origin,
                secret,
                locale,
                courseSlug = sharedSlug,
                traceparent = SuppliedTrace,
                tenants,
                presentation = new { label = PresentationTypeLabel, fields = PresentationFields }
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
                    case "foundation-normal":
                    case "foundation-repeat":
                    case "foundation-missing":
                        var foundationRequests = observed.Requests.Skip(foundationPosition).ToArray();
                        foundationRequests.Should().HaveCount(checkpoint == "foundation-missing" ? 5 : 3,
                            "metadata, document/layout and page share one request-local admission and content operation");
                        foundationRequests.Count(request => request.Path == "/api/v1/public/site").Should()
                            .Be(checkpoint == "foundation-missing" ? 4 : 2);
                        foundationRequests.Count(request => request.Path.StartsWith("/api/v1/public/courses", StringComparison.Ordinal)).Should().Be(1);
                        foundationRequests.Single(request => request.Path.StartsWith("/api/v1/public/courses", StringComparison.Ordinal))
                            .Locale.Should().Be(locale, "UI configuration cannot change the exact content API locale");
                        foundationPosition = observed.Requests.Length;
                        break;
                    case "foundation-catalog-english":
                    case "foundation-catalog-yoga":
                    case "foundation-catalog-turkish":
                    case "foundation-course-yoga":
                    case "foundation-course-turkish":
                    case "foundation-restricted":
                    case "foundation-empty":
                    case "pagination-catalog-first":
                    case "pagination-catalog-next":
                    case "pagination-catalog-restart":
                    case "pagination-outline-first":
                    case "pagination-outline-next":
                    case "pagination-outline-restart":
                        var productRequests = observed.Requests.Skip(foundationPosition).ToArray();
                        productRequests.Should().HaveCount(3, "an unchanged page uses one shared content operation");
                        productRequests.Count(request => request.Path == "/api/v1/public/site").Should().Be(2);
                        productRequests.Count(request => request.Path.StartsWith("/api/v1/public/courses", StringComparison.Ordinal)).Should().Be(1);
                        productRequests.Single(request => request.Path.StartsWith("/api/v1/public/courses", StringComparison.Ordinal))
                            .Locale.Should().Be(checkpoint switch
                            {
                                "foundation-catalog-turkish" or "foundation-course-turkish" =>
                                    SeedData.Yoga.Curriculum!.Locales.Single(row => row.IsDefault).Locale,
                                "foundation-empty" => "ar",
                                _ => locale
                            }, "only the admitted content locale reaches Education");
                        foundationPosition = observed.Requests.Length;
                        break;
                    case "foundation-cross-tenant":
                        var crossTenantRequests = observed.Requests.Skip(foundationPosition).ToArray();
                        crossTenantRequests.Should().HaveCount(5, "missing cross-tenant detail and followed status use the approved redirect chain");
                        crossTenantRequests.Count(request => request.Path == "/api/v1/public/site").Should().Be(4);
                        crossTenantRequests.Count(request => request.Path.StartsWith("/api/v1/public/courses", StringComparison.Ordinal)).Should().Be(1);
                        foundationPosition = observed.Requests.Length;
                        break;
                    case "foundation-status":
                    case "foundation-head":
                    case "foundation-refused":
                    case "foundation-canonical":
                        var statusRequests = observed.Requests.Skip(foundationPosition).ToArray();
                        statusRequests.Should().HaveCount(checkpoint switch
                        {
                            "foundation-status" => 8,
                            "foundation-head" => 2,
                            "foundation-canonical" => 3,
                            _ => 2
                        });
                        statusRequests.Should().OnlyContain(request => request.Path == "/api/v1/public/site",
                            "status pages and admission refusals must never query Education");
                        foundationPosition = observed.Requests.Length;
                        break;
                    case "presentation-english":
                    case "presentation-yoga-en":
                    case "presentation-yoga-tr":
                    case "presentation-rsc-english":
                    case "presentation-rsc-yoga-tr":
                    case "presentation-exact-pin":
                    case "presentation-swapped":
                    case "presentation-rsc-swapped":
                    case "presentation-yoga-unchanged":
                    case "presentation-empty":
                    case "presentation-unavailable":
                    case "presentation-restored":
                        var presentationRequests = observed.Requests.Skip(foundationPosition).ToArray();
                        presentationRequests.Should().HaveCount(3,
                            "the unchanged lesson route shares one exact content read across metadata, document and page");
                        presentationRequests.Count(request => request.Path == "/api/v1/public/site").Should().Be(2);
                        var lessonRequest = presentationRequests.Single(request => request.Path != "/api/v1/public/site");
                        lessonRequest.Path.Should().Contain("/lessons/");
                        lessonRequest.Locale.Should().Be(checkpoint is "presentation-yoga-tr" or "presentation-rsc-yoga-tr" or "presentation-yoga-unchanged"
                            ? SeedData.Yoga.Curriculum!.Locales.Single(row => row.IsDefault).Locale : locale);
                        foundationPosition = observed.Requests.Length;
                        break;
                    case "presentation-cross-host":
                    case "presentation-protected":
                        var hiddenLessonRequests = observed.Requests.Skip(foundationPosition).ToArray();
                        hiddenLessonRequests.Should().HaveCount(5,
                            "one refused lesson document and its localized status redirect use the real bootstrap chain");
                        hiddenLessonRequests.Count(request => request.Path == "/api/v1/public/site").Should().Be(4);
                        hiddenLessonRequests.Single(request => request.Path != "/api/v1/public/site").Locale.Should()
                            .Be(checkpoint == "presentation-protected" ? SeedData.Yoga.Curriculum!.Locales.Single(row => row.IsDefault).Locale : locale);
                        foundationPosition = observed.Requests.Length;
                        break;
                    case "presentation-publish-revision":
                        observed.Requests.Length.Should().Be(foundationPosition, "fixture setup makes no public API calls");
                        await AddPresentationRevisionAsync();
                        presentationRevision = true;
                        break;
                    case "presentation-pin-revision":
                        observed.Requests.Length.Should().Be(foundationPosition);
                        await SetPresentationLessonAsync(firstCourse, SeedData.English.Curriculum!.ContentType.SchemaVersion + 1,
                            JsonSerializer.Serialize(PresentationFields.ToDictionary(field => field.Name, field => field.Value), JsonOptions));
                        break;
                    case "presentation-make-empty":
                        observed.Requests.Length.Should().Be(foundationPosition);
                        await SetPresentationLessonAsync(firstCourse, SeedData.English.Curriculum!.ContentType.SchemaVersion + 1, "{}");
                        break;
                    case "presentation-make-unavailable":
                        observed.Requests.Length.Should().Be(foundationPosition);
                        await SetPresentationLessonAsync(firstCourse, SeedData.English.Curriculum!.ContentType.SchemaVersion + 1,
                            "{\"fixture_script\":42,\"fixture_hidden\":\"unavailable-private-body-canary\"}");
                        break;
                    case "presentation-restore-lesson":
                        observed.Requests.Length.Should().Be(foundationPosition);
                        await RestorePresentationLessonAsync(firstCourse);
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
            if (mode == "transport") checkpoints.Should().Equal("build-complete", "trace-supplied", "trace-missing", "trace-malformed", "protocol-before", "protocol-after", "stock-before", "stock-after", "make-draft", "restore-published", "verified");
            else if (mode == "foundation") checkpoints.Should().Equal("build-complete", "foundation-normal", "foundation-repeat",
                "foundation-catalog-english", "foundation-catalog-yoga", "foundation-catalog-turkish",
                "foundation-course-yoga", "foundation-course-turkish",
                "foundation-restricted", "foundation-empty", "foundation-cross-tenant", "foundation-missing",
                "foundation-status", "foundation-head", "foundation-refused", "foundation-canonical", "stock-before", "stock-after", "verified");
            else if (mode == "foundation-pagination") checkpoints.Should().Equal("build-complete", "pagination-catalog-first", "pagination-catalog-next",
                "pagination-catalog-restart", "pagination-outline-first", "pagination-outline-next",
                "pagination-outline-restart", "verified");
            else checkpoints.Should().Equal("build-complete", "presentation-english", "presentation-yoga-en", "presentation-yoga-tr",
                "presentation-rsc-english", "presentation-rsc-yoga-tr", "presentation-publish-revision", "presentation-exact-pin",
                "presentation-pin-revision", "presentation-swapped", "presentation-rsc-swapped", "presentation-yoga-unchanged",
                "presentation-make-empty", "presentation-empty", "presentation-make-unavailable", "presentation-unavailable",
                "presentation-restore-lesson", "presentation-restored", "presentation-cross-host", "presentation-protected", "verified");
            if (mode == "foundation") observed.Requests.Should().HaveCount(52,
                "normal/repeat, both hosts and languages, restricted/empty/cross-tenant, status and refusal checks fit one visitor window");
            if (mode == "foundation-pagination") observed.Requests.Should().HaveCount(18,
                "six actual catalog/outline documents each make exactly three API calls");
            if (mode == "presentation") observed.Requests.Should().HaveCount(46,
                "twelve actual lesson HTML/RSC representations and two followed hidden lessons fit the unchanged visitor budget");
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
                if (started) process.StandardInput.Close();
                if (started && !process.HasExited)
                {
                    using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    try { await process.WaitForExitAsync(shutdown.Token); }
                    catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
                }
            }
            finally
            {
                if (draft) await SetStatusAsync(firstCourse, firstCourse.Status);
                if (foundationLocales)
                    await fixture.ExecuteAsync(SeedData.English,
                        "DELETE FROM tenant_locales WHERE tenant_id=@tenant AND locale IN ('ar','tr-TR','tr')");
                if (paginationRows) await RemovePaginationRowsAsync(firstCourse);
                if (presentationRevision) await RemovePresentationRevisionAsync(firstCourse);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Renderer_control_pipe_failure_cleans_up_the_owned_build(bool brokenOutput)
    {
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "learnstack-renderer-control-" + Guid.NewGuid().ToString("N"));
        using var process = RendererProcess(RepositoryRoot(), temporaryRoot);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var started = false;
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            started = process.Start();
            started.Should().BeTrue();
            var errors = BoundedErrorsAsync(process.StandardError, deadline.Token);
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                apiOrigin = "http://127.0.0.1:1",
                secret = new string('s', 43),
                tenants = new[] { new { host = "first.example" }, new { host = "second.example" } }
            }, JsonOptions));
            // Wait for a real compiler acquisition before failing its parent
            // pipe. No listener or API call is needed before build-complete.
            string? ownedRoot = null;
            while (!process.HasExited)
            {
                ownedRoot = Directory.EnumerateDirectories(temporaryRoot, "learnstack-public-rendering-*").SingleOrDefault();
                if (ownedRoot is not null && Directory.Exists(Path.Combine(ownedRoot, "frontend/apps/web/.next"))) break;
                await Task.Delay(50, deadline.Token);
            }
            ownedRoot.Should().NotBeNull("the actual renderer must acquire its private build root");
            Directory.Exists(Path.Combine(ownedRoot!, "frontend/apps/web/.next")).Should().BeTrue("the owned compiler must have started");
            if (brokenOutput) process.StandardOutput.Close();
            else process.StandardInput.Close();
            await process.WaitForExitAsync(deadline.Token);
            var errorOutput = await errors;
            process.ExitCode.Should().Be(1, errorOutput);
            if (brokenOutput) errorOutput.Should().BeEmpty("the build-complete write must trigger EPIPE cancellation after successful configured builds");
            Directory.EnumerateFileSystemEntries(temporaryRoot).Should().BeEmpty(
                "pipe failure must join child cleanup before deleting the owned build/TLS root");
        }
        finally
        {
            if (started && !process.HasExited)
            {
                process.StandardInput.Close();
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try { await process.WaitForExitAsync(shutdown.Token); }
                catch (OperationCanceledException) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); }
            }
            if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("ordinary-route", false)]
    [InlineData("ordinary-version", false)]
    [InlineData("bare-version", false)]
    [InlineData("short-mac", false)]
    [InlineData("long-mac", false)]
    [InlineData("padded-mac", false)]
    [InlineData("empty-payload", false)]
    [InlineData("embedded-version", false)]
    [InlineData("envelope", true)]
    [InlineData("long-envelope", true)]
    [InlineData("header", true)]
    [InlineData("secret", true)]
    public void Api_log_containment_recognizes_private_values_without_matching_ordinary_versions(string scenario, bool expected)
    {
        const string secret = "test-owned-private-hop-credential";
        var mac = new string('m', 43);
        var value = scenario switch
        {
            "ordinary-route" => "/api/v1/public/courses",
            "ordinary-version" => "API contract v1.2.3 remains supported",
            "bare-version" => "v1.",
            "short-mac" => "v1.payload." + new string('m', 42),
            "long-mac" => "v1.payload." + new string('m', 44),
            "padded-mac" => "v1.payload." + mac + "=",
            "empty-payload" => "v1.." + mac,
            "embedded-version" => "av1.payload." + mac,
            "envelope" => "v1.payload." + mac,
            "long-envelope" => "v1." + new string('p', 12_000) + "." + mac,
            "header" => "X-LearnStack-Ingress-Provenance",
            "secret" => secret,
            _ => throw new InvalidOperationException("Unknown log containment control.")
        };
        var observation = new RenderingObservation(secret);
        observation.Emit(new LogEvent(DateTimeOffset.UtcNow, LogEventLevel.Information, null,
            new MessageTemplateParser().Parse("Fixture {Value}"),
            [new LogEventProperty("Value", new ScalarValue(value))]));
        observation.Logs.Should().Be(1, "the control must pass through the real structured log formatter");
        observation.PrivateLog.Should().Be(expected);
    }

    private Task SetStatusAsync(SeedCourse course, string status) => fixture.ExecuteOwnerFixtureAsync(
        SeedData.English,
        "UPDATE courses SET status=@status WHERE tenant_id=@tenant AND id=@id",
        new Dictionary<string, object> { ["id"] = course.Id, ["status"] = status.ToLowerInvariant() });

    private Task AddPresentationRevisionAsync()
    {
        var type = SeedData.English.Curriculum!.ContentType;
        var fields = PresentationFields.Select(field => new
        {
            name = field.Name,
            label = new Dictionary<string, string> { [field.Label.Locale] = field.Label.Value }
        }).Append(new { name = "fixture_optional", label = new Dictionary<string, string> { ["en"] = "Omitted optional field" } }).ToArray();
        // This new optional-string profile satisfies ADR-0051. Its order differs
        // from JSONB key order; the immutable seeded revision is never rewritten.
        var schema = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["type"] = "object",
            ["properties"] = fields.ToDictionary(field => field.name, _ => new { type = "string" }),
            ["additionalProperties"] = false,
            ["x-fields"] = fields
        }, JsonOptions);
        return fixture.ExecuteOwnerFixtureAsync(SeedData.English,
            """
            UPDATE tenant_content_types SET status='Deprecated'
            WHERE tenant_id=@tenant AND id=@original AND status='Active';
            INSERT INTO tenant_content_types(id,tenant_id,key,schema_version,status,display_name,
                                             json_schema,renderer_key,created_at,created_by)
            VALUES(@revision,@tenant,@key,@version,'Active',CAST(@label AS jsonb),
                   CAST(@schema AS jsonb),'default-card',now(),@actor);
            UPDATE customization_generations SET generation=generation+1 WHERE tenant_id=@tenant;
            """,
            new Dictionary<string, object>
            {
                ["original"] = type.Id,
                ["revision"] = PresentationRevisionId,
                ["key"] = type.Key,
                ["version"] = type.SchemaVersion + 1,
                ["label"] = JsonSerializer.Serialize(new Dictionary<string, string>
                {
                    [PresentationTypeLabel.Locale] = PresentationTypeLabel.Value
                }, JsonOptions),
                ["schema"] = schema
            });
    }

    private Task SetPresentationLessonAsync(SeedCourse course, int version, string body) => fixture.ExecuteOwnerFixtureAsync(
        SeedData.English,
        """
        UPDATE lessons SET content_type_schema_version=@version WHERE tenant_id=@tenant AND id=@lesson;
        UPDATE lesson_translations SET body=CAST(@body AS jsonb)
        WHERE tenant_id=@tenant AND lesson_id=@lesson AND locale='en';
        """,
        new Dictionary<string, object>
        {
            ["lesson"] = course.Lessons.First(row => row.Status == "Published").Id,
            ["version"] = version,
            ["body"] = body
        });

    private Task RestorePresentationLessonAsync(SeedCourse course)
    {
        var lesson = course.Lessons.First(row => row.Status == "Published");
        return SetPresentationLessonAsync(course, lesson.ContentTypeSchemaVersion,
            lesson.Translations.Single(row => row.Locale == "en").Body);
    }

    private async Task RemovePresentationRevisionAsync(SeedCourse course)
    {
        // Also runs after any failed checkpoint; no test-owned pin/body, lifecycle
        // or generation increment survives into the shared fixture's next case.
        await RestorePresentationLessonAsync(course);
        await fixture.ExecuteOwnerFixtureAsync(SeedData.English,
            """
            DELETE FROM tenant_content_types WHERE tenant_id=@tenant AND id=@revision;
            UPDATE tenant_content_types SET status='Active' WHERE tenant_id=@tenant AND id=@original;
            UPDATE customization_generations SET generation=generation-1 WHERE tenant_id=@tenant;
            """,
            new Dictionary<string, object>
            {
                ["revision"] = PresentationRevisionId,
                ["original"] = SeedData.English.Curriculum!.ContentType.Id
            });
    }

    private Task AddPaginationRowsAsync(SeedCourse course)
    {
        var lesson = course.Lessons.First(row => row.Status == "Published");
        return fixture.ExecuteOwnerFixtureAsync(SeedData.English,
            """
        INSERT INTO courses(id,tenant_id,organization_id,slug_key,content_access,status,created_at,created_by)
        SELECT md5('p6-fixture-course-' || n)::uuid,@tenant,NULL,'p6-fixture-course-' || n,
               'public','published',now() + n * interval '1 millisecond',@actor
        FROM generate_series(1,21) AS n;
        INSERT INTO course_translations(course_id,tenant_id,organization_id,locale,title,summary,slug)
        SELECT md5('p6-fixture-course-' || n)::uuid,@tenant,NULL,'en',
               'Fixture course ' || lpad(n::text,2,'0'),'Fixture catalog continuation',
               'p6-fixture-course-' || n
        FROM generate_series(1,21) AS n;
        INSERT INTO lessons(id,tenant_id,organization_id,course_id,sort,content_type_key,
                            content_type_schema_version,status,created_at,created_by)
        SELECT md5('p6-fixture-lesson-' || n)::uuid,@tenant,NULL,@course,1000+n,
               @contentType,@contentVersion,'published',now(),@actor
        FROM generate_series(1,21) AS n;
        INSERT INTO lesson_translations(lesson_id,tenant_id,organization_id,locale,title,slug,body)
        SELECT md5('p6-fixture-lesson-' || n)::uuid,@tenant,NULL,'en',
               'Fixture lesson ' || lpad(n::text,2,'0'),'p6-fixture-lesson-' || n,
               CAST(@body AS jsonb)
        FROM generate_series(1,21) AS n;
        """,
            new Dictionary<string, object>
            {
                ["course"] = course.Id,
                ["contentType"] = lesson.ContentTypeKey,
                ["contentVersion"] = lesson.ContentTypeSchemaVersion,
                ["body"] = lesson.Translations.Single(row => row.Locale == "en").Body
            });
    }

    private Task RemovePaginationRowsAsync(SeedCourse course) => fixture.ExecuteOwnerFixtureAsync(
        SeedData.English,
        """
        DELETE FROM lesson_translations WHERE tenant_id=@tenant AND slug LIKE 'p6-fixture-lesson-%';
        DELETE FROM lessons WHERE tenant_id=@tenant AND course_id=@course
            AND id IN (SELECT md5('p6-fixture-lesson-' || n)::uuid FROM generate_series(1,21) AS n);
        DELETE FROM course_translations WHERE tenant_id=@tenant AND slug LIKE 'p6-fixture-course-%';
        DELETE FROM courses WHERE tenant_id=@tenant AND slug_key LIKE 'p6-fixture-course-%';
        """,
        new Dictionary<string, object> { ["course"] = course.Id });

    private static object TenantInput(SeedTenant tenant, SeedCourse course, string locale)
    {
        var lesson = course.Lessons.First(row => row.Status == "Published");
        var translation = lesson.Translations.Single(row => row.Locale == locale);
        var defaultLocale = tenant.Curriculum!.Locales.Single(row => row.IsDefault).Locale;
        var defaultCourse = course.Translations.Single(row => row.Locale == defaultLocale);
        var defaultLesson = lesson.Translations.Single(row => row.Locale == defaultLocale);
        var visible = tenant.Curriculum.Courses.Where(row => row.Status == "Published"
            && (row.OrganizationId is null || (tenant.MapHostToDefaultOrganization
                && row.OrganizationId == tenant.DefaultOrganization.OrganizationId))).ToArray();
        var hidden = tenant.Curriculum.Courses.Except(visible).ToArray();
        var restricted = visible.FirstOrDefault(row => row.ContentAccess == "enrollment_required");
        var restrictedTranslation = restricted?.Translations.Single(row => row.Locale == locale);
        var restrictedDefault = restricted?.Translations.Single(row => row.Locale == defaultLocale);
        var catalogEn = visible.Select(row => row.Translations.Single(translation => translation.Locale == locale))
            .Select(row => new { row.Title, row.Slug }).ToArray();
        var catalogDefault = visible.Select(row => row.Translations.Single(translation => translation.Locale == defaultLocale))
            .Select(row => new { row.Title, row.Slug }).ToArray();
        using var body = JsonDocument.Parse(translation.Body);
        return new
        {
            host = tenant.Host,
            name = tenant.DisplayName,
            courseTitle = course.Translations.Single(row => row.Locale == locale).Title,
            courseSummary = course.Translations.Single(row => row.Locale == locale).Summary,
            lessonSlug = translation.Slug,
            lessonTitle = translation.Title,
            lessonText = body.RootElement.EnumerateObject().First().Value.GetString(),
            defaultLocale,
            defaultCourseSlug = defaultCourse.Slug,
            defaultCourseTitle = defaultCourse.Title,
            defaultCourseSummary = defaultCourse.Summary,
            defaultLessonSlug = defaultLesson.Slug,
            defaultLessonTitle = defaultLesson.Title,
            content = SeedPresentation(tenant, translation, locale),
            defaultContent = SeedPresentation(tenant, defaultLesson, defaultLocale),
            restrictedSlug = restrictedTranslation?.Slug,
            restrictedDefaultSlug = restrictedDefault?.Slug,
            restrictedDefaultLessonSlug = restricted?.Lessons.First(row => row.Status == "Published")
                .Translations.Single(row => row.Locale == defaultLocale).Slug,
            restrictedTitle = restrictedTranslation?.Title,
            restrictedSummary = restrictedTranslation?.Summary,
            restrictedCanaries = LessonCanaries(restricted, locale),
            restrictedDefaultCanaries = LessonCanaries(restricted, defaultLocale),
            catalogEn,
            catalogDefault,
            hiddenEn = hidden.Select(row => row.Translations.Single(translation => translation.Locale == locale).Title).ToArray(),
            hiddenDefault = hidden.Select(row => row.Translations.Single(translation => translation.Locale == defaultLocale).Title).ToArray()
        };
    }

    private static object SeedPresentation(SeedTenant tenant, SeedLessonTranslation translation, string locale)
    {
        var type = tenant.Curriculum!.ContentType;
        using var schema = JsonDocument.Parse(type.JsonSchema);
        using var body = JsonDocument.Parse(translation.Body);
        return new
        {
            label = new PresentationLabel(type.DisplayName[locale], locale),
            fields = schema.RootElement.GetProperty("x-fields").EnumerateArray().Select(field =>
            {
                var name = field.GetProperty("name").GetString()!;
                return new PresentationField(name,
                    new PresentationLabel(field.GetProperty("label").GetProperty(locale).GetString()!, locale),
                    body.RootElement.GetProperty(name).GetString()!);
            }).ToArray()
        };
    }

    private static string[] LessonCanaries(SeedCourse? course, string locale) => course?.Lessons
        .SelectMany(row => row.Translations.Where(item => item.Locale == locale))
        .SelectMany(item =>
        {
            using var content = JsonDocument.Parse(item.Body);
            return new[] { item.Title, item.Slug }.Concat(content.RootElement.EnumerateObject()
                .Select(field => field.Value.GetString()!)).ToArray();
        }).Distinct(StringComparer.Ordinal).ToArray() ?? [];

    private sealed record PresentationLabel(string Value, string Locale);
    private sealed record PresentationField(string Name, PresentationLabel Label, string Value);

    private static Process RendererProcess(string root, string? temporaryRoot = null)
    {
        var start = new ProcessStartInfo("node")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(root, "frontend/apps/web/scripts/verify-public-rendering.mjs"));
        // Configuration travels on stdin; only the tool path and an explicitly
        // verified TLS policy enter from this process's environment.
        start.Environment.Clear();
        start.Environment["PATH"] = Environment.GetEnvironmentVariable("PATH");
        start.Environment["NODE_TLS_REJECT_UNAUTHORIZED"] = "1";
        if (temporaryRoot is not null) start.Environment["TMPDIR"] = temporaryRoot;
        return new Process { StartInfo = start };
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

    private sealed record ObservedRequest(string Path, string Locale, string TraceId, bool ValidHop);

    /// <summary>Observes the real Kestrel request without changing API resolution or transactions.</summary>
    private sealed class RenderingObservation(string secret) : IStartupFilter, ILogEventSink
    {
        // Match the complete unpadded wire shape, rather than ordinary v1 route
        // or version text. Retain boundaries so truncated/padded tokens cannot
        // accidentally satisfy the containment proof.
        private static readonly Regex PrivateEnvelope = new(
            @"(?<![A-Za-z0-9_.-])v1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]{43}(?![A-Za-z0-9_.=-])",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
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
                || PrivateEnvelope.IsMatch(value))
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
                    _requests.Enqueue(new(context.Request.Path.Value!, context.Request.Query["locale"].ToString(), Activity.Current?.TraceId.ToHexString() ?? "", validHop));
                }
                await continuation();
            });
            next(app);
        };
    }
}
