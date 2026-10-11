using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Collection(SharedSchema.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PublicLocaleEvolutionHttpTests(SchemaFixture schema, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string Root = "/api/v1/public/courses";
    private const string Site = "/api/v1/public/site";
    private const string FrenchCourseSlug = "pratique-francaise";
    private const string FrenchLessonSlug = "posture-stable";
    private const string FrenchCourseTitle = "Pratique en français";
    private const string FrenchLessonTitle = "Une posture stable";
    private const string FrenchSummary = "Un cours rédigé en français.";
    // Body-member order deliberately differs from the pinned presentation order.
    private const string FrenchBody = """{"breathing":"Respirez lentement.","pose":"Posture stable","instruction":"Tenez la posture."}""";
    private static SeedTenant Yoga => SeedData.Yoga;

    [Fact]
    public async Task Third_locale_authored_through_api_commands_serves_exact_new_urls_without_changing_existing_responses_or_migrations()
    {
        await using var database = await SeededDatabaseAsync();
        var observation = new PublicReadObservation();
        await using var host = ApiHost(database, observation);
        using var client = Client(host, Yoga.Host);
        var context = new SeedTenantContext(Yoga.TenantId, null);
        var organizationContext = new SeedTenantContext(Yoga.TenantId, Yoga.DefaultOrganization.OrganizationId);
        var baselinePaths = new[]
        {
            Root + "?locale=en", Root + "?locale=tr-TR",
            Root + "/foundation?locale=en", Root + "/studyo-temelleri?locale=tr-TR",
            Root + "/foundation/lessons/tree-pose?locale=en",
            Root + "/studyo-temelleri/lessons/agac-durusu?locale=tr-TR",
        };
        var before = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var path in baselinePaths) before.Add(path, await JsonAsync(client, path));
        var siteBefore = await JsonAsync(client, Site);
        var historiesBefore = await MigrationHistoryAsync(database.MigrationConnectionString);
        var tenantBefore = (await SendAsync(host, context, new GetTenantSeedStateQuery())).State!;
        var existingCourse = Yoga.Curriculum!.Courses[1];
        var existingLesson = existingCourse.Lessons[0];
        var courseBefore = (await SendAsync(host, organizationContext, new GetCourseSeedStateQuery(existingCourse.Id))).State!;
        var lessonBefore = (await SendAsync(host, organizationContext, new GetLessonSeedStateQuery(existingLesson.Id))).State!;

        var locales = await SendAsync(host, context, new AddTenantLocaleCommand(tenantBefore.Version, "fr", true, false, 0));
        locales.Version.Should().Be(tenantBefore.Version + 1);
        locales.Locales.Where(locale => locale.Locale != "fr").Should().BeEquivalentTo(
            tenantBefore.Locales, options => options.WithStrictOrdering());
        locales.Locales.Single(locale => locale.Locale == "fr").Should().Be(new TenantLocaleDto("fr", true, false, 0));

        // Published seeds cannot accept translations. Author a new French-only
        // draft through the existing writers, then publish each root normally.
        var course = await SendAsync(host, context, new CreateCourseCommand(Guid.CreateVersion7(), "third-locale", "public"));
        course.Status.Should().Be("Draft");
        course = await SendAsync(host, context, new AddCourseTranslationCommand(
            course.Id, course.Version, "fr", FrenchCourseTitle, FrenchSummary, FrenchCourseSlug));
        var contentType = Yoga.Curriculum.ContentType;
        var lesson = await SendAsync(host, context, new CreateLessonCommand(
            Guid.CreateVersion7(), course.Id, 0, contentType.Key, contentType.SchemaVersion));
        lesson.Status.Should().Be("Draft");
        lesson = await SendAsync(host, context, new AddLessonTranslationCommand(
            lesson.Id, lesson.Version, "fr", FrenchLessonTitle, FrenchLessonSlug, FrenchBody));
        lesson = await SendAsync(host, context, new PublishLessonCommand(lesson.Id, lesson.Version));
        course = await SendAsync(host, context, new PublishCourseCommand(course.Id, course.Version));
        lesson.Status.Should().Be("Published");
        course.Status.Should().Be("Published");
        course.ContentAccess.Should().Be("public");

        var auditsAfterAuthoring = await CountsAsync(database.AppConnectionString, organizationContext);
        var catalog = await JsonAsync(client, Root + "?locale=fr");
        var detail = await JsonAsync(client, $"{Root}/{FrenchCourseSlug}?locale=fr");
        var lessonDetail = await JsonAsync(client, $"{Root}/{FrenchCourseSlug}/lessons/{FrenchLessonSlug}?locale=fr");
        AssertFrenchResponses(catalog, detail, lessonDetail);
        foreach (var (path, expected) in before)
            JsonNode.DeepEquals(await JsonAsync(client, path), expected).Should().BeTrue(
                "existing exact-locale DTOs, ordered fields/lessons and alternate pairs must remain unchanged at {0}", path);

        foreach (var locale in new[] { "en", "tr-TR" })
        {
            await NotFoundAsync(client, $"{Root}/{FrenchCourseSlug}?locale={locale}");
            await NotFoundAsync(client, $"{Root}/{FrenchCourseSlug}/lessons/{FrenchLessonSlug}?locale={locale}");
        }
        foreach (var path in new[] { "/foundation", "/studyo-temelleri", "/foundation/lessons/tree-pose", "/studyo-temelleri/lessons/agac-durusu" })
            await NotFoundAsync(client, Root + path + "?locale=fr");
        // English's enabled locale admits the request, then tenant isolation hides
        // the new Yoga course and lesson independently of locale admission.
        await NotFoundAsync(client, $"http://{SeedData.English.Host}{Root}/{FrenchCourseSlug}?locale=en");
        await NotFoundAsync(client, $"http://{SeedData.English.Host}{Root}/{FrenchCourseSlug}/lessons/{FrenchLessonSlug}?locale=en");
        await NotFoundAsync(client, $"{Root}/foundation/lessons/{FrenchLessonSlug}?locale=fr");

        var siteAfter = await JsonAsync(client, Site);
        var expectedLocales = locales.Locales.Where(locale => locale.IsEnabled).Select(locale => locale.Locale).ToArray();
        siteBefore["enabledLocales"]!.AsArray().Select(locale => locale!.GetValue<string>()).Should().Equal("tr-TR", "en");
        expectedLocales.Should().Equal("fr", "tr-TR", "en");
        siteAfter["enabledLocales"]!.AsArray().Select(locale => locale!.GetValue<string>()).Should().Equal(expectedLocales);
        siteBefore.AsObject().Remove("enabledLocales");
        siteAfter.AsObject().Remove("enabledLocales");
        JsonNode.DeepEquals(siteAfter, siteBefore).Should().BeTrue("only the exact enabled-locale delta may change the bootstrap");
        (await SendAsync(host, organizationContext, new GetCourseSeedStateQuery(existingCourse.Id))).State
            .Should().BeEquivalentTo(courseBefore, options => options.WithStrictOrdering());
        (await SendAsync(host, organizationContext, new GetLessonSeedStateQuery(existingLesson.Id))).State
            .Should().BeEquivalentTo(lessonBefore, options => options.WithStrictOrdering());
        var authoredLesson = (await SendAsync(host, context, new GetLessonSeedStateQuery(lesson.Id))).State!;
        authoredLesson.TenantId.Should().Be(Yoga.TenantId);
        authoredLesson.OrganizationId.Should().BeNull();
        authoredLesson.CourseId.Should().Be(course.Id);
        authoredLesson.ContentTypeKey.Should().Be(contentType.Key);
        authoredLesson.ContentTypeSchemaVersion.Should().Be(contentType.SchemaVersion);
        authoredLesson.Translations.Should().ContainSingle().Which.Locale.Should().Be("fr");
        (await MigrationHistoryAsync(database.MigrationConnectionString)).Should().BeEquivalentTo(
            historiesBefore, options => options.WithStrictOrdering());
        (await CountsAsync(database.AppConnectionString, organizationContext)).Should().Be(auditsAfterAuthoring,
            "successful and hidden public reads must add no setting or audit rows");
        observation.Reads.Should().BeGreaterThan(0, "the production reader must positively observe learnstack_app under physical READ ONLY");
    }

    [Fact]
    public async Task Organization_branding_writer_is_refused_with_denied_audit_and_mapped_host_keeps_tenant_palette()
    {
        await using var database = await SeededDatabaseAsync();
        var observation = new PublicReadObservation();
        await using var host = ApiHost(database, observation);
        using var client = Client(host, Yoga.Host);
        var context = new SeedTenantContext(Yoga.TenantId, Yoga.DefaultOrganization.OrganizationId);
        var before = await JsonAsync(client, Site);
        var mapped = await SendAsync(host, context, new GetHostMappingSeedStateQuery(Yoga.Host));
        mapped.State!.OrganizationId.Should().Be(Yoga.DefaultOrganization.OrganizationId);
        mapped.State.IsPubliclyLive.Should().BeTrue();
        var settingBefore = (await SendAsync(host, context, new GetSettingSeedStateQuery(Yoga.Curriculum!.Theme.Id))).State!;
        settingBefore.OrganizationId.Should().BeNull();
        JsonNode.DeepEquals(before["theme"], JsonNode.Parse(settingBefore.Value)).Should().BeTrue();
        var countsBefore = await CountsAsync(database.AppConnectionString, context);
        var deniedBefore = await BrandingDenialsAsync(database.AppConnectionString, context);
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var accessor = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
            var previous = accessor.Current;
            accessor.Current = context;
            try
            {
                var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new SetTenantBrandingCommand(
                    settingBefore.Id, """{"primary":"#2345aa","background":"#ffffff","foreground":"#111111","muted":"#555555"}""", settingBefore.Version));
                result.IsFailure.Should().BeTrue();
                result.Error!.Code.Should().Be("resource_scope_violation");
            }
            finally { accessor.Current = previous; }
        }
        (await SendAsync(host, context, new GetSettingSeedStateQuery(settingBefore.Id))).State.Should().BeEquivalentTo(settingBefore);
        var after = await JsonAsync(client, Site);
        JsonNode.DeepEquals(after, before).Should().BeTrue();
        var countsAfter = await CountsAsync(database.AppConnectionString, context);
        countsAfter.Settings.Should().Be(countsBefore.Settings);
        countsAfter.SuccessfulBrandingAudits.Should().Be(countsBefore.SuccessfulBrandingAudits);
        countsAfter.Audits.Should().Be(countsBefore.Audits + 1, "the normal MUST audit pipeline retains the denied authoring attempt");
        (await BrandingDenialsAsync(database.AppConnectionString, context)).Should().Be(deniedBefore + 1);
        observation.Reads.Should().BeGreaterThan(0);
    }

    private async Task<DisposableSchemaDatabase> SeededDatabaseAsync()
    {
        var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        try
        {
            await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
            var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance), NullLogger<SeedRunner>.Instance);
            (await runner.RunAsync(CancellationToken.None)).Should().Be(0);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }

    private WebApplicationFactory<Program> ApiHost(DisposableSchemaDatabase database, PublicReadObservation observation) =>
        factory.WithWebHostBuilder(builder => builder
            .UseEnvironment(Environments.Development)
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .UseSetting("ConnectionStrings:PlatformAdmin", database.PlatformConnectionString)
            .ConfigureTestServices(services =>
            {
                services.AddSingleton(observation);
                services.AddScoped<PublicTenantConfigurationReader>();
                // This existing observer checks the actual enlisted transaction's
                // READ ONLY, current_user, superuser and BYPASSRLS flags.
                services.AddScoped<IPublicTenantConfigurationReader, ObservedPublicConfigurationReader>();
            }));

    private static HttpClient Client(WebApplicationFactory<Program> host, string hostname) =>
        host.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"http://{hostname}/") });

    private static async Task<T> SendAsync<T>(WebApplicationFactory<Program> host, ITenantContext context, IRequest<Result<T>> request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var accessor = scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>();
        var previous = accessor.Current;
        accessor.Current = context;
        try
        {
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
            result.IsSuccess.Should().BeTrue("the normal API pipeline must accept {0}: {1}", request.GetType().Name, result.Error?.Code);
            return result.Value!;
        }
        finally { accessor.Current = previous; }
    }

    private static void AssertFrenchResponses(JsonNode catalog, JsonNode course, JsonNode lesson)
    {
        var summary = JsonSerializer.SerializeToNode(new { slug = FrenchCourseSlug, title = FrenchCourseTitle, summary = FrenchSummary, contentAccess = "public", level = (object?)null });
        catalog["locale"]!.GetValue<string>().Should().Be("fr");
        catalog["items"]!.AsArray().Should().ContainSingle();
        JsonNode.DeepEquals(catalog["items"]![0], summary).Should().BeTrue();
        course["locale"]!.GetValue<string>().Should().Be("fr");
        JsonNode.DeepEquals(course["course"], summary).Should().BeTrue();
        course["alternates"]!.AsArray().Should().BeEmpty();
        var outline = course["lessons"]!["items"]!.AsArray();
        outline.Should().ContainSingle();
        JsonNode.DeepEquals(outline[0], JsonSerializer.SerializeToNode(new { slug = FrenchLessonSlug, title = FrenchLessonTitle, sort = 0 })).Should().BeTrue();
        lesson["locale"]!.GetValue<string>().Should().Be("fr");
        JsonNode.DeepEquals(lesson["course"], JsonSerializer.SerializeToNode(new { slug = FrenchCourseSlug, title = FrenchCourseTitle })).Should().BeTrue();
        JsonNode.DeepEquals(lesson["lesson"], JsonSerializer.SerializeToNode(new { slug = FrenchLessonSlug, title = FrenchLessonTitle })).Should().BeTrue();
        lesson["alternates"]!.AsArray().Should().BeEmpty();
        var content = lesson["content"]!;
        content["state"]!.GetValue<string>().Should().Be("ready");
        content["rendererKey"]!.GetValue<string>().Should().Be("default-card");
        // French body/title are exact; definition labels use the accepted
        // requested -> tenant-default -> en fallback independently.
        JsonNode.DeepEquals(content["label"], JsonSerializer.SerializeToNode(new { value = "Asana duruşu", locale = "tr-TR" })).Should().BeTrue();
        var expectedFields = JsonSerializer.SerializeToNode(new[]
        {
            new { name = "pose", label = new { value = "Duruş", locale = "tr-TR" }, value = "Posture stable" },
            new { name = "instruction", label = new { value = "Yönerge", locale = "tr-TR" }, value = "Tenez la posture." },
            new { name = "breathing", label = new { value = "Nefes", locale = "tr-TR" }, value = "Respirez lentement." },
        });
        JsonNode.DeepEquals(content["fields"], expectedFields).Should().BeTrue("the pinned field order and exact French values survive localization");
    }

    private static async Task<JsonNode> JsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    private static async Task NotFoundAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        response.Headers.CacheControl?.NoStore.Should().BeTrue();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = (await response.Content.ReadFromJsonAsync<JsonNode>())!;
        problem["code"]!.GetValue<string>().Should().Be("not_found");
    }

    private static async Task<Dictionary<string, List<HistoryRow>>> MigrationHistoryAsync(string connectionString)
    {
        // Owner access is limited to migration metadata, which the app role is
        // deliberately not granted. Every behavior/isolation read uses the app role.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            await readOnly.ExecuteNonQueryAsync();
        var discovered = await SchemaQueries.ReadStringsAsync(connection,
            "SELECT tablename FROM pg_tables WHERE schemaname='public' AND tablename LIKE '__ef_migrations_history_%' ORDER BY tablename", transaction);
        discovered.Should().BeEquivalentTo(MigrationChains.HistoryTables);
        var histories = new Dictionary<string, List<HistoryRow>>(StringComparer.Ordinal);
        foreach (var table in discovered)
        {
            var rows = new List<HistoryRow>();
            await using var command = new NpgsqlCommand($"SELECT \"MigrationId\", \"ProductVersion\" FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\" ORDER BY \"MigrationId\"", connection, transaction);
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(new HistoryRow(reader.GetString(0), reader.GetString(1)));
            rows.Should().NotBeEmpty("each deployed migration chain must be represented");
            histories.Add(table, rows);
        }
        return histories;
    }

    private static async Task<WriteCounts> CountsAsync(string connectionString, SeedTenantContext context)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, context.TenantId.Value);
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", context.OrganizationId?.Value.ToString("D") ?? "");
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await using var command = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM tenant_settings),
                   (SELECT count(*) FROM audit_log),
                   (SELECT count(*) FROM audit_log WHERE operation='tenancy.setting.write' AND outcome='success')
            """, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new WriteCounts(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }

    private static async Task<long> BrandingDenialsAsync(string connectionString, SeedTenantContext context)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await SchemaQueries.SetTenantAsync(connection, transaction, context.TenantId.Value);
        await SchemaQueries.SetSettingAsync(connection, transaction, "app.organization_id", context.OrganizationId!.Value.Value.ToString("D"));
        await SchemaQueries.AssertApplicationRoleAsync(connection);
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM audit_log
            WHERE operation='tenancy.setting.write' AND outcome='denied'
              AND error_key='lockey_resource_scope_violation' AND organization_id=@organization
            """, connection, transaction);
        command.Parameters.AddWithValue("organization", context.OrganizationId.Value.Value);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private sealed record HistoryRow(string MigrationId, string ProductVersion);
    private sealed record WriteCounts(long Settings, long Audits, long SuccessfulBrandingAudits);
}
