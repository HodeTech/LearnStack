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
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
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
public sealed class PublicLocaleEvolutionHttpTests(SchemaFixture schema, ConstructionOnlyApiFactory factory)
    : IClassFixture<ConstructionOnlyApiFactory>
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

        var englishContext = new SeedTenantContext(SeedData.English.TenantId, null);
        var englishBefore = (await SendAsync(host, englishContext, new GetTenantSeedStateQuery())).State!;
        await SendAsync(host, englishContext, new AddTenantLocaleCommand(englishBefore.Version, "fr", true, false, 1));

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
        // Both hosts now admit French, and these exact URLs succeeded on Yoga.
        // The foreign refusal therefore discriminates tenant isolation from an
        // unsupported locale or a translation missing on the owning tenant.
        var englishFrench = await JsonAsync(client, $"http://{SeedData.English.Host}{Root}?locale=fr");
        englishFrench["locale"]!.GetValue<string>().Should().Be("fr");
        englishFrench["items"]!.AsArray().Should().BeEmpty();
        await NotFoundAsync(client, $"http://{SeedData.English.Host}{Root}/{FrenchCourseSlug}?locale=fr");
        await NotFoundAsync(client, $"http://{SeedData.English.Host}{Root}/{FrenchCourseSlug}/lessons/{FrenchLessonSlug}?locale=fr");
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

    [Fact]
    public async Task Public_host_locale_and_organization_contracts_hold_together_with_exact_request_connection_translation_scope()
    {
        await using var database = await SeededDatabaseAsync();
        var observation = new PublicReadObservation();
        var scopes = new TranslationScopeObservation();
        await using var host = ApiHost(database, observation, scopes);
        using var client = Client(host, Yoga.Host);
        var context = new SeedTenantContext(Yoga.TenantId, null);
        const string tenantHost = "yoga-tenant-scope.example.test";
        var mapping = await SendAsync(host, context, new MapHostToTenantCommand(tenantHost, null, true, true));
        mapping.TenantId.Should().Be(Yoga.TenantId);
        mapping.OrganizationId.Should().BeNull();
        var tenant = (await SendAsync(host, context, new GetTenantSeedStateQuery())).State!;
        await SendAsync(host, context, new AddTenantLocaleCommand(tenant.Version, "fr", true, false, 2));
        var probe = await AuthorLocaleProbeAsync(host, context);
        scopes.Expect(SeedData.English.Host, SeedData.English, null, null);
        scopes.Expect(Yoga.Host, Yoga, Yoga.DefaultOrganization.OrganizationId, probe);
        scopes.Expect(tenantHost, Yoga, null, probe);
        var requests = 0;

        // The same supported route and locale positively resolve B's unique
        // course, then conceal it on A's already live host.
        var yogaOnly = Yoga.Curriculum!.Courses.Single(course => course.SlugKey == "shared-practice");
        var yogaOnlySlug = yogaOnly.Translations.Single(row => row.Locale == "en").Slug;
        JsonNode.DeepEquals(await Read(Yoga.Host, $"/{yogaOnlySlug}?locale=en"),
            ExpectedSeedCourse(Yoga, yogaOnly.Id, "en")).Should().BeTrue();
        await Hidden(SeedData.English.Host, $"/{yogaOnlySlug}?locale=en");

        // Stable source identities select the full expected public payloads;
        // neither accepted public DTO exposes its source ID.
        var englishShared = SeedData.English.Curriculum!.Courses.Single(course => course.SlugKey == "foundations");
        var yogaShared = Yoga.Curriculum.Courses.Single(course => course.SlugKey == "studio-foundations");
        var englishDetail = await Read(SeedData.English.Host, "/foundation?locale=en");
        var yogaDetail = await Read(Yoga.Host, "/foundation?locale=en");
        JsonNode.DeepEquals(englishDetail, ExpectedSeedCourse(SeedData.English, englishShared.Id, "en")).Should().BeTrue();
        JsonNode.DeepEquals(yogaDetail, ExpectedSeedCourse(Yoga, yogaShared.Id, "en")).Should().BeTrue();
        JsonNode.DeepEquals(englishDetail, yogaDetail).Should().BeFalse();

        var englishProbe = await Read(Yoga.Host, "/locale-probe?locale=en");
        englishProbe["course"]!["title"]!.GetValue<string>().Should().Be("Locale probe");
        Slugs(englishProbe["lessons"]!["items"]!).Should().Equal("english-lesson");
        var turkishProbe = await Read(Yoga.Host, "/yerel-prova?locale=tr-TR");
        Slugs(turkishProbe["lessons"]!["items"]!).Should().Equal("yalniz-turkce");
        var translatedLesson = await Read(Yoga.Host, "/locale-probe/lessons/english-lesson?locale=en");
        translatedLesson["lesson"]!["title"]!.GetValue<string>().Should().Be("English lesson");
        var otherLocaleLesson = await Read(Yoga.Host, "/yerel-prova/lessons/yalniz-turkce?locale=tr-TR");
        otherLocaleLesson["lesson"]!["title"]!.GetValue<string>().Should().Be("Yalnız Türkçe");
        await Hidden(Yoga.Host, "/locale-probe/lessons/yalniz-turkce?locale=en");
        await Hidden(Yoga.Host, "/locale-probe?locale=fr");
        var frenchCatalog = await Read(Yoga.Host, "?locale=fr");
        frenchCatalog["locale"]!.GetValue<string>().Should().Be("fr");
        Slugs(frenchCatalog["items"]!).Should().BeEmpty("French is enabled but none of these published courses has a French translation");

        var organizationCatalog = await Read(Yoga.Host, "?locale=en");
        Slugs(organizationCatalog["items"]!).Should().Equal(
            SeedCatalogSlugs(Yoga, Yoga.DefaultOrganization.OrganizationId, "en").Append("locale-probe"));
        Slugs((await Read(Yoga.Host, "?locale=tr-TR"))["items"]!).Should().Contain("yerel-prova");
        var sibling = Yoga.Curriculum.Courses.Single(course => course.OrganizationId == Yoga.SecondOrganization.OrganizationId);
        await Hidden(Yoga.Host, "/" + sibling.Translations.Single(row => row.Locale == "en").Slug + "?locale=en");
        var tenantCatalog = await Read(tenantHost, "?locale=en");
        Slugs(tenantCatalog["items"]!).Should().Equal(SeedCatalogSlugs(Yoga, null, "en").Append("locale-probe"));
        await Hidden(tenantHost, "/foundation?locale=en");
        JsonNode.DeepEquals(await Read(tenantHost, $"/{yogaOnlySlug}?locale=en"),
            ExpectedSeedCourse(Yoga, yogaOnly.Id, "en")).Should().BeTrue();

        // Each successful/hidden HTTP request passes the raw satellite observer
        // exactly once on its own physically read-only application transaction.
        scopes.Reads.Should().Be(requests);
        observation.Reads.Should().Be(requests);

        async Task<JsonNode> Read(string hostname, string path)
        {
            requests++;
            return await JsonAsync(client, $"http://{hostname}{Root}{path}");
        }
        async Task Hidden(string hostname, string path)
        {
            requests++;
            await NotFoundAsync(client, $"http://{hostname}{Root}{path}");
        }
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

    private WebApplicationFactory<Program> ApiHost(DisposableSchemaDatabase database, PublicReadObservation observation,
        TranslationScopeObservation? scopes = null) =>
        factory.WithWebHostBuilder(builder => builder
            .UseEnvironment(Environments.Development)
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .UseSetting("ConnectionStrings:PlatformAdmin", database.PlatformConnectionString)
            .ConfigureTestServices(services =>
            {
                services.AddSingleton(observation);
                services.AddHttpContextAccessor();
                services.AddScoped<PublicTenantConfigurationReader>();
                // This existing observer checks the actual enlisted transaction's
                // READ ONLY, current_user, superuser and BYPASSRLS flags.
                services.AddScoped<ObservedPublicConfigurationReader>();
                services.AddScoped<IPublicTenantConfigurationReader>(provider => scopes is null
                    ? provider.GetRequiredService<ObservedPublicConfigurationReader>()
                    : new RequestTranslationScopeReader(provider.GetRequiredService<ObservedPublicConfigurationReader>(),
                        provider.GetRequiredService<IUnitOfWork>(), provider.GetRequiredService<ITenantContext>(),
                        provider.GetRequiredService<IHttpContextAccessor>(), scopes));
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

    private static async Task<LocaleProbe> AuthorLocaleProbeAsync(WebApplicationFactory<Program> host, SeedTenantContext context)
    {
        var course = await SendAsync(host, context, new CreateCourseCommand(Guid.CreateVersion7(), "locale-probe", "public"));
        course = await SendAsync(host, context, new AddCourseTranslationCommand(course.Id, course.Version,
            "en", "Locale probe", null, "locale-probe"));
        course = await SendAsync(host, context, new AddCourseTranslationCommand(course.Id, course.Version,
            "tr-TR", "Yerel prova", null, "yerel-prova"));
        var contentType = Yoga.Curriculum!.ContentType;
        const string body = """{"pose":"Probe pose","instruction":"Hold steadily.","breathing":"Breathe slowly."}""";
        var turkish = await SendAsync(host, context, new CreateLessonCommand(Guid.CreateVersion7(), course.Id, 0,
            contentType.Key, contentType.SchemaVersion));
        turkish = await SendAsync(host, context, new AddLessonTranslationCommand(turkish.Id, turkish.Version,
            "tr-TR", "Yalnız Türkçe", "yalniz-turkce", body));
        await SendAsync(host, context, new PublishLessonCommand(turkish.Id, turkish.Version));
        var english = await SendAsync(host, context, new CreateLessonCommand(Guid.CreateVersion7(), course.Id, 1,
            contentType.Key, contentType.SchemaVersion));
        english = await SendAsync(host, context, new AddLessonTranslationCommand(english.Id, english.Version,
            "en", "English lesson", "english-lesson", body));
        await SendAsync(host, context, new PublishLessonCommand(english.Id, english.Version));
        await SendAsync(host, context, new PublishCourseCommand(course.Id, course.Version));
        return new LocaleProbe(context.TenantId, course.Id, turkish.Id, english.Id);
    }

    private static JsonNode ExpectedSeedCourse(SeedTenant tenant, Guid sourceId, string locale)
    {
        var curriculum = tenant.Curriculum!;
        var course = curriculum.Courses.Single(row => row.Id == sourceId);
        var translation = course.Translations.Single(row => row.Locale == locale);
        curriculum.Taxonomy.Key.Should().Be(course.LevelTaxonomyKey);
        curriculum.Taxonomy.SchemaVersion.Should().Be(course.LevelTaxonomySchemaVersion);
        var band = curriculum.Taxonomy.Bands.Single(row => row.Key == course.LevelBandKey);
        var alternates = curriculum.Locales.Where(row => row.IsEnabled && row.Locale != locale)
            .OrderBy(row => row.Sort).ThenBy(row => row.Locale, StringComparer.Ordinal)
            .Select(row => course.Translations.SingleOrDefault(item => item.Locale == row.Locale))
            .Where(row => row is not null).Select(row => new { locale = row!.Locale, slug = row.Slug }).ToArray();
        object? outline = course.ContentAccess == "public" ? new
        {
            items = course.Lessons.Where(row => row.Status == "Published" && row.Translations.Any(item => item.Locale == locale))
                .OrderBy(row => row.Sort).ThenBy(row => row.Id).Select(row => new
                {
                    slug = row.Translations.Single(item => item.Locale == locale).Slug,
                    title = row.Translations.Single(item => item.Locale == locale).Title,
                    sort = row.Sort,
                }).ToArray(),
            pageInfo = new { nextCursor = (string?)null, previousCursor = (string?)null, hasNext = false, hasPrevious = false },
        } : null;
        return JsonSerializer.SerializeToNode(new
        {
            locale,
            course = new
            {
                slug = translation.Slug,
                title = translation.Title,
                summary = translation.Summary,
                contentAccess = course.ContentAccess,
                level = new { state = "ready", label = new { value = band.DisplayName[locale], locale } },
            },
            alternates,
            lessons = outline,
        })!;
    }

    private static string[] SeedCatalogSlugs(SeedTenant tenant, OrganizationId? organization, string locale) =>
        tenant.Curriculum!.Courses.Where(row => row.Status == "Published"
            && (row.OrganizationId is null || row.OrganizationId == organization))
            .Select(row => row.Translations.SingleOrDefault(item => item.Locale == locale))
            .Where(row => row is not null).Select(row => row!.Slug).ToArray();

    private static string[] Slugs(JsonNode items) => items.AsArray().Select(item => item!["slug"]!.GetValue<string>()).ToArray();

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
        (response.Headers.CacheControl?.NoStore).Should().BeTrue();
        return (await response.Content.ReadFromJsonAsync<JsonNode>())!;
    }

    private static async Task NotFoundAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        (response.Headers.CacheControl?.NoStore).Should().BeTrue();
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
    private sealed record LocaleProbe(TenantId TenantId, Guid CourseId, Guid TurkishLessonId, Guid EnglishLessonId);
    private sealed record TranslationRow(Guid TenantId, Guid? OrganizationId, Guid RootId, string Locale, string Slug);
    private sealed record TranslationScope(TenantId TenantId, OrganizationId? OrganizationId,
        TranslationRow[] Courses, TranslationRow[] Lessons);

    private sealed class TranslationScopeObservation
    {
        private readonly Dictionary<string, TranslationScope> _expected = new(StringComparer.Ordinal);
        private int _reads;
        public int Reads => Volatile.Read(ref _reads);

        public void Expect(string hostname, SeedTenant tenant, OrganizationId? organization, LocaleProbe? probe)
        {
            // Raw scope deliberately includes every seeded translation regardless
            // of publication/access/locale; these are RLS assertions, not the
            // narrower public-eligibility projection.
            var courses = tenant.Curriculum!.Courses
                .Where(row => row.OrganizationId is null || row.OrganizationId == organization).ToArray();
            var courseRows = courses.SelectMany(course => course.Translations.Select(row =>
                new TranslationRow(tenant.TenantId.Value, course.OrganizationId?.Value, course.Id, row.Locale, row.Slug))).ToList();
            var lessonRows = courses.SelectMany(course => course.Lessons.SelectMany(lesson => lesson.Translations.Select(row =>
                new TranslationRow(tenant.TenantId.Value, course.OrganizationId?.Value, lesson.Id, row.Locale, row.Slug)))).ToList();
            if (probe is not null)
            {
                probe.TenantId.Should().Be(tenant.TenantId);
                courseRows.AddRange([
                    new TranslationRow(probe.TenantId.Value, null, probe.CourseId, "en", "locale-probe"),
                    new TranslationRow(probe.TenantId.Value, null, probe.CourseId, "tr-TR", "yerel-prova"),
                ]);
                lessonRows.AddRange([
                    new TranslationRow(probe.TenantId.Value, null, probe.TurkishLessonId, "tr-TR", "yalniz-turkce"),
                    new TranslationRow(probe.TenantId.Value, null, probe.EnglishLessonId, "en", "english-lesson"),
                ]);
            }
            courseRows.Should().NotBeEmpty();
            lessonRows.Should().NotBeEmpty();
            _expected.Add(hostname, new TranslationScope(tenant.TenantId, organization, courseRows.ToArray(), lessonRows.ToArray()));
        }

        public async Task AssertAsync(string hostname, ITenantContext context, IUnitOfWork unit, CancellationToken cancellationToken)
        {
            var expected = _expected[hostname];
            context.TenantId.Should().Be(expected.TenantId);
            context.OrganizationId.Should().Be(expected.OrganizationId);
            context.HostScope.Should().NotBeNull();
            context.HostScope!.TenantId.Should().Be(expected.TenantId);
            context.HostScope.OrganizationId.Should().Be(expected.OrganizationId);
            unit.Mode.Should().Be(TransactionMode.ReadOnly);
            unit.HasActiveTransaction.Should().BeTrue();
            unit.IsTenantContextIssuedOn(unit.Transaction).Should().BeTrue();
            await SchemaQueries.AssertApplicationRoleAsync(unit.Connection);
            // No WHERE clause, EF query filter, replacement connection, or scope
            // announcement: only the request's already enlisted RLS frame filters
            // these two complete satellite-table reads.
            var courses = await ReadRowsAsync(unit,
                "SELECT tenant_id, organization_id, course_id, locale, slug FROM course_translations", cancellationToken);
            var lessons = await ReadRowsAsync(unit,
                "SELECT tenant_id, organization_id, lesson_id, locale, slug FROM lesson_translations", cancellationToken);
            courses.Should().NotBeEmpty();
            lessons.Should().NotBeEmpty();
            courses.Should().BeEquivalentTo(expected.Courses, "raw course translations must match the exact scope of host {0}", hostname);
            lessons.Should().BeEquivalentTo(expected.Lessons, "raw lesson translations must match the exact scope of host {0}", hostname);
            Interlocked.Increment(ref _reads);
        }

        private static async Task<List<TranslationRow>> ReadRowsAsync(IUnitOfWork unit, string sql, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction?)unit.Transaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var rows = new List<TranslationRow>();
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new TranslationRow(reader.GetGuid(0), reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.GetGuid(2), reader.GetString(3), reader.GetString(4)));
            return rows;
        }
    }

    private sealed class RequestTranslationScopeReader(ObservedPublicConfigurationReader reader, IUnitOfWork unit,
        ITenantContext context, IHttpContextAccessor http, TranslationScopeObservation observation) : IPublicTenantConfigurationReader
    {
        public async Task<Result<PublicTenantConfiguration>> ReadAsync(CancellationToken cancellationToken)
        {
            var result = await reader.ReadAsync(cancellationToken);
            await observation.AssertAsync(http.HttpContext!.Request.Host.Host, context, unit, cancellationToken);
            return result;
        }
    }
}
