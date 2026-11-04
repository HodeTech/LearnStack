using LearnStack.Modules.Customization.Application.Contracts.Customization;
using System.Collections.Immutable;
using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LearnStack.Tools.Seeder;

/// <summary>Converges declared seed acts through contextual requests and exact postconditions.</summary>
/// <remarks>
/// Each request owns a fresh composed scope. Completed acts skip before sending a writer,
/// including published translations. A typed race gets one fresh completed-state check,
/// never a blind retry, overwrite, scope setter, database context or private transaction.
/// </remarks>
public sealed class SeedRunner(Func<ITenantContext?, ServiceProvider> compose, ILogger<SeedRunner> logger)
{
    private readonly List<TenantId> _createdThisRun = [];
    private readonly List<TenantId> _qualifiedOriginsObserved = [];
    public ImmutableArray<TenantId> FreshlyCreatedTenantIds { get; private set; } = [];
    public ImmutableArray<TenantId> QualifiedOriginTenantIds { get; private set; } = [];

    public async Task<int> RunAsync(CancellationToken cancellationToken, IReadOnlyList<SeedTenant>? tenants = null)
    {
        _createdThisRun.Clear();
        _qualifiedOriginsObserved.Clear();
        FreshlyCreatedTenantIds = [];
        QualifiedOriginTenantIds = [];
        var declared = tenants ?? SeedData.All;
        foreach (var tenant in declared) SeedVerification.Declaration(tenant);
        foreach (var tenant in declared) await SeedTenantAsync(tenant, cancellationToken);
        FreshlyCreatedTenantIds = [.. _createdThisRun.Distinct()];
        QualifiedOriginTenantIds = [.. _qualifiedOriginsObserved.Distinct()];
        return 0;
    }

    private async Task SeedTenantAsync(SeedTenant tenant, CancellationToken ct)
    {
        var context = new SeedTenantContext(tenant.TenantId, null);
        async Task<TenantSeedDto?> ReadTenant() => (await ReadAsync(context, new GetTenantSeedStateQuery(), ct)).State;
        var existingTenant = await ReadTenant();
        var existingDefault = (await ReadAsync(context, new GetOrganizationSeedStateQuery(tenant.DefaultOrganization.OrganizationId), ct)).State;
        var existingSecond = (await ReadAsync(context, new GetOrganizationSeedStateQuery(tenant.SecondOrganization.OrganizationId), ct)).State;
        var existingHost = (await ReadAsync(context, new GetHostMappingSeedStateQuery(tenant.Host), ct)).State;
        var tenantInstalled = SeedVerification.Tenant(existingTenant, tenant);
        if (tenantInstalled && existingTenant!.AdoptedSeedOriginVersion == 2)
            _qualifiedOriginsObserved.Add(tenant.TenantId);
        var defaultInstalled = SeedVerification.Organization(existingDefault, tenant.DefaultOrganization, tenant, "default organization");
        var secondInstalled = SeedVerification.Organization(existingSecond, tenant.SecondOrganization, tenant, "second organization");
        var hostInstalled = SeedVerification.Host(existingHost, tenant);
        var contentComplete = await VerifyContentCompletionAsync(tenant, context, ct);
        if (contentComplete)
        {
            SeedVerification.Require(tenantInstalled && defaultInstalled && secondInstalled && hostInstalled, tenant, "retained installation");
            // Mutable Tenancy fields, including removed locales, are preserved only after all
            // declared content acts prove the original installation finished. No writer is sent.
            return;
        }
        SeedVerification.RequirePristineTenant(existingTenant, tenant);
        SeedVerification.RequirePristineOrganization(existingDefault, tenant.DefaultOrganization, tenant);
        SeedVerification.RequirePristineOrganization(existingSecond, tenant.SecondOrganization, tenant);
        SeedVerification.Require(existingHost is null || existingHost.IsActive && existingHost.IsPubliclyLive, tenant, "pristine host mapping");
        if (tenant.Curriculum is { } incomplete)
            _ = SeedVerification.Theme((await ReadAsync(context, new GetSettingSeedStateQuery(incomplete.Theme.Id), ct)).State,
                incomplete.Theme, tenant);
        await ActAsync(tenant, context, "tenant", ReadTenant, row => SeedVerification.Tenant(row, tenant),
            _ => new ProvisionTenantCommand(tenant.TenantId, tenant.Slug, tenant.DisplayName,
                tenant.DefaultOrganization.OrganizationId, tenant.DefaultOrganization.Slug, tenant.DefaultOrganization.DisplayName), ct,
            unresolvedWrite: true);
        var defaultOrg = (await ReadAsync(context, new GetOrganizationSeedStateQuery(tenant.DefaultOrganization.OrganizationId), ct)).State;
        SeedVerification.Require(SeedVerification.Organization(defaultOrg, tenant.DefaultOrganization, tenant, "default organization"), tenant, "default organization");
        await ActAsync(tenant, context, "second organization",
            async () => (await ReadAsync(context, new GetOrganizationSeedStateQuery(tenant.SecondOrganization.OrganizationId), ct)).State,
            row => SeedVerification.Organization(row, tenant.SecondOrganization, tenant, "second organization"),
            _ => new CreateOrganizationCommand(tenant.SecondOrganization.OrganizationId, tenant.SecondOrganization.Slug, tenant.SecondOrganization.DisplayName), ct);
        await ActAsync(tenant, context, "host mapping",
            async () => (await ReadAsync(context, new GetHostMappingSeedStateQuery(tenant.Host), ct)).State,
            row => SeedVerification.Host(row, tenant),
            _ => new MapHostToTenantCommand(tenant.Host, tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId : null, true, true), ct);

        if (tenant.Curriculum is { } curriculum)
            foreach (var locale in curriculum.Locales.OrderByDescending(locale => locale.IsDefault).ThenBy(locale => locale.Sort))
                await ActAsync(tenant, context, "locale", ReadTenant, row => SeedVerification.Locale(row, locale, tenant),
                    row => new AddTenantLocaleCommand(Version(row), locale.Locale, locale.IsEnabled, locale.IsDefault, locale.Sort), ct);
        await SeedBuiltInsAsync(tenant, context, ct);
        foreach (var type in SeedData.ContentTypes(tenant)) await SeedContentTypeAsync(tenant, context, type, ct);
        foreach (var taxonomy in SeedData.Taxonomies(tenant)) await SeedTaxonomyAsync(tenant, context, taxonomy, ct);
        if (tenant.Curriculum is not { } content) return;
        await ActAsync(tenant, context, "branding",
            async () => (await ReadAsync(context, new GetSettingSeedStateQuery(content.Theme.Id), ct)).State,
            row => SeedVerification.Theme(row, content.Theme, tenant),
            _ => new SetTenantBrandingCommand(content.Theme.Id, content.Theme.Value, null), ct);
        foreach (var course in content.Courses) await SeedCourseAsync(tenant, course, ct);
        foreach (var course in content.Courses) await PublishCourseAsync(tenant, course, ct);
    }

    private async Task<bool> VerifyContentCompletionAsync(SeedTenant tenant, ITenantContext context, CancellationToken ct)
    {
        var complete = true;
        var card = (await ReadAsync(context, new GetActiveContentTypeSeedRevisionQuery(BuiltInCustomizations.Card.Key), ct)).State;
        complete &= SeedVerification.BuiltInContentType(card is null ? null
            : (await ReadAsync(context, new GetContentTypeSeedStateQuery(card.Id), ct)).State, tenant);
        var plain = (await ReadAsync(context, new GetActiveTaxonomySeedRevisionQuery(BuiltInCustomizations.Plain.Key), ct)).State;
        complete &= SeedVerification.BuiltInTaxonomy(plain is null ? null
            : (await ReadAsync(context, new GetTaxonomySeedStateQuery(plain.Id), ct)).State, tenant);
        foreach (var type in SeedData.ContentTypes(tenant))
        {
            var active = (await ReadAsync(context, new GetActiveContentTypeSeedRevisionQuery(type.Key), ct)).State;
            SeedVerification.Require(active is null || active.Id == type.Id, tenant, "content type active revision");
            complete &= SeedVerification.ContentType((await ReadAsync(context, new GetContentTypeSeedStateQuery(type.Id), ct)).State,
                type, tenant, published: true);
        }
        foreach (var taxonomy in SeedData.Taxonomies(tenant))
        {
            var active = (await ReadAsync(context, new GetActiveTaxonomySeedRevisionQuery(taxonomy.Key), ct)).State;
            SeedVerification.Require(active is null || active.Id == taxonomy.Id, tenant, "level taxonomy active revision");
            complete &= SeedVerification.Taxonomy((await ReadAsync(context, new GetTaxonomySeedStateQuery(taxonomy.Id), ct)).State,
                taxonomy, tenant, published: true);
        }
        if (tenant.Curriculum is not { } content) return complete;
        complete &= SeedVerification.Theme((await ReadAsync(context, new GetSettingSeedStateQuery(content.Theme.Id), ct)).State,
            content.Theme, tenant, preserveInstalled: true);
        foreach (var course in content.Courses)
        {
            var courseContext = new SeedTenantContext(tenant.TenantId, course.OrganizationId);
            var row = (await ReadAsync(courseContext, new GetCourseSeedStateQuery(course.Id), ct)).State;
            var exists = SeedVerification.Course(row, course, tenant);
            complete &= exists && row!.Status == course.Status && row.Translations.Length == course.Translations.Length;
            foreach (var lesson in course.Lessons)
            {
                var lessonRow = (await ReadAsync(courseContext, new GetLessonSeedStateQuery(lesson.Id), ct)).State;
                exists = SeedVerification.Lesson(lessonRow, course, lesson, tenant);
                complete &= exists && lessonRow!.Status == lesson.Status && lessonRow.Translations.Length == lesson.Translations.Length;
            }
        }
        return complete;
    }

    private async Task SeedBuiltInsAsync(SeedTenant tenant, ITenantContext context, CancellationToken ct)
    {
        async Task<ContentTypeSeedDto?> ReadContentType()
        {
            var active = (await ReadAsync(context, new GetActiveContentTypeSeedRevisionQuery(BuiltInCustomizations.Card.Key), ct)).State;
            if (active is null) return null;
            SeedVerification.Require(active.Id != Guid.Empty, tenant, "built-in content type identity");
            var row = (await ReadAsync(context, new GetContentTypeSeedStateQuery(active.Id), ct)).State;
            SeedVerification.Require(row is not null && row.Id == active.Id, tenant, "built-in content type identity");
            return row;
        }
        await ActAsync(tenant, context, "built-in content type", ReadContentType,
            row => SeedVerification.BuiltInContentType(row, tenant),
            _ => new InitializeTenantContentTypeDefaultCommand(), ct);

        async Task<TaxonomySeedDto?> ReadTaxonomy()
        {
            var active = (await ReadAsync(context, new GetActiveTaxonomySeedRevisionQuery(BuiltInCustomizations.Plain.Key), ct)).State;
            if (active is null) return null;
            SeedVerification.Require(active.Id != Guid.Empty, tenant, "built-in level taxonomy identity");
            var row = (await ReadAsync(context, new GetTaxonomySeedStateQuery(active.Id), ct)).State;
            SeedVerification.Require(row is not null && row.Id == active.Id, tenant, "built-in level taxonomy identity");
            return row;
        }
        await ActAsync(tenant, context, "built-in level taxonomy", ReadTaxonomy,
            row => SeedVerification.BuiltInTaxonomy(row, tenant),
            _ => new InitializeTenantLevelTaxonomyDefaultCommand(), ct);
    }

    private async Task SeedContentTypeAsync(SeedTenant tenant, ITenantContext context, SeedContentType type, CancellationToken ct)
    {
        async Task<ContentTypeSeedDto?> Read()
        {
            var active = (await ReadAsync(context, new GetActiveContentTypeSeedRevisionQuery(type.Key), ct)).State;
            SeedVerification.Require(active is null || active.Id == type.Id, tenant, "content type active revision");
            return (await ReadAsync(context, new GetContentTypeSeedStateQuery(type.Id), ct)).State;
        }
        await ActAsync(tenant, context, "content type", Read, row => SeedVerification.ContentType(row, type, tenant, false),
            _ => new RegisterTenantContentTypeCommand(type.Id, type.Key, type.SchemaVersion, type.DisplayName, type.JsonSchema, type.RendererKey), ct);
        await ActAsync(tenant, context, "content type publication", Read, row => SeedVerification.ContentType(row, type, tenant, true),
            _ => new PublishTenantContentTypeCommand(type.Id, RequireNoIncumbent: true), ct);
    }

    private async Task SeedTaxonomyAsync(SeedTenant tenant, ITenantContext context, SeedTaxonomy taxonomy, CancellationToken ct)
    {
        async Task<TaxonomySeedDto?> Read()
        {
            var active = (await ReadAsync(context, new GetActiveTaxonomySeedRevisionQuery(taxonomy.Key), ct)).State;
            SeedVerification.Require(active is null || active.Id == taxonomy.Id, tenant, "level taxonomy active revision");
            return (await ReadAsync(context, new GetTaxonomySeedStateQuery(taxonomy.Id), ct)).State;
        }
        await ActAsync(tenant, context, "level taxonomy", Read, row => SeedVerification.Taxonomy(row, taxonomy, tenant, false),
            _ => new RegisterTenantLevelTaxonomyCommand(taxonomy.Id, taxonomy.Key, taxonomy.SchemaVersion, taxonomy.DisplayName,
                [.. taxonomy.Bands.Select(band => new TaxonomyItemInput(band.Key, band.DisplayName, band.Sort, band.Metadata))]), ct);
        await ActAsync(tenant, context, "level taxonomy publication", Read, row => SeedVerification.Taxonomy(row, taxonomy, tenant, true),
            _ => new PublishTenantLevelTaxonomyCommand(taxonomy.Id, RequireNoIncumbent: true), ct);
    }

    private async Task SeedCourseAsync(SeedTenant tenant, SeedCourse course, CancellationToken ct)
    {
        var context = new SeedTenantContext(tenant.TenantId, course.OrganizationId);
        async Task<CourseSeedDto?> Read() => (await ReadAsync(context, new GetCourseSeedStateQuery(course.Id), ct)).State;
        await ActAsync(tenant, context, "course", Read, row => SeedVerification.Course(row, course, tenant),
            _ => new CreateCourseCommand(course.Id, course.SlugKey, course.ContentAccess,
                course.LevelTaxonomyKey, course.LevelTaxonomySchemaVersion, course.LevelBandKey), ct);
        foreach (var translation in course.Translations)
            await ActAsync(tenant, context, "course translation", Read,
                row => SeedVerification.CourseTranslation(row, course, translation, tenant),
                row => new AddCourseTranslationCommand(course.Id, Version(row), translation.Locale, translation.Title, translation.Summary, translation.Slug), ct);
        foreach (var lesson in course.Lessons)
        {
            async Task<LessonSeedDto?> ReadLesson() => (await ReadAsync(context, new GetLessonSeedStateQuery(lesson.Id), ct)).State;
            await ActAsync(tenant, context, "lesson", ReadLesson, row => SeedVerification.Lesson(row, course, lesson, tenant),
                _ => new CreateLessonCommand(lesson.Id, course.Id, lesson.Sort, lesson.ContentTypeKey, lesson.ContentTypeSchemaVersion), ct);
            foreach (var translation in lesson.Translations)
                await ActAsync(tenant, context, "lesson translation", ReadLesson,
                    row => SeedVerification.LessonTranslation(row, course, lesson, translation, tenant),
                    row => new AddLessonTranslationCommand(lesson.Id, Version(row), translation.Locale, translation.Title, translation.Slug, translation.Body), ct);
        }
    }

    private async Task PublishCourseAsync(SeedTenant tenant, SeedCourse course, CancellationToken ct)
    {
        var context = new SeedTenantContext(tenant.TenantId, course.OrganizationId);
        foreach (var lesson in course.Lessons)
        {
            async Task<LessonSeedDto?> ReadLesson() => (await ReadAsync(context, new GetLessonSeedStateQuery(lesson.Id), ct)).State;
            if (lesson.Status == "Published")
                await ActAsync(tenant, context, "lesson publication", ReadLesson,
                    row => SeedVerification.LessonPublication(row, course, lesson, tenant),
                    row => new PublishLessonCommand(lesson.Id, Version(row)), ct);
            else SeedVerification.Require(SeedVerification.LessonPublication(await ReadLesson(), course, lesson, tenant), tenant, "lesson final state");
        }
        async Task<CourseSeedDto?> ReadCourse() => (await ReadAsync(context, new GetCourseSeedStateQuery(course.Id), ct)).State;
        if (course.Status == "Published")
            await ActAsync(tenant, context, "course publication", ReadCourse,
                row => SeedVerification.CoursePublication(row, course, tenant),
                row => new PublishCourseCommand(course.Id, Version(row)), ct);
        else SeedVerification.Require(SeedVerification.CoursePublication(await ReadCourse(), course, tenant), tenant, "course final state");
    }

    private async Task ActAsync<TState, TResponse>(SeedTenant tenant, ITenantContext context, string act,
        Func<Task<TState?>> read, Func<TState?, bool> completed, Func<TState?, IRequest<Result<TResponse>>> command,
        CancellationToken ct, bool unresolvedWrite = false) where TState : class
    {
        var before = await read();
        if (completed(before))
        {
            SeedRunnerLog.AlreadyPresent(logger, act, tenant.Slug);
            return;
        }
        var result = await SendAsync(unresolvedWrite ? null : context, command(before), ct);
        if (!result.IsSuccess && (result.Error is not { } error || !IsRace(error)))
            throw new InvalidOperationException($"Seeding the {act} for '{tenant.Slug}' failed with '{result.Error?.Code}'. Resolve the cause and re-run.");
        // The writer has committed or returned a typed race. Neither outcome proves
        // the declared act completed; read its exact postcondition in a fresh scope.
        SeedVerification.Require(completed(await read()), tenant, act);
        if (result.IsSuccess && unresolvedWrite && before is null) _createdThisRun.Add(tenant.TenantId);
        if (result.IsSuccess) SeedRunnerLog.Seeded(logger, act, tenant.Slug);
        else SeedRunnerLog.AlreadyPresent(logger, act, tenant.Slug);
    }

    private async Task<T> ReadAsync<T>(ITenantContext context, IRequest<Result<T>> query, CancellationToken ct)
    {
        var result = await SendAsync(context, query, ct);
        if (result.IsSuccess && result.Value is { } value) return value;
        throw new InvalidOperationException($"Reading seed verification failed with '{result.Error?.Code}'.");
    }
    private async Task<Result<T>> SendAsync<T>(ITenantContext? context, IRequest<Result<T>> request, CancellationToken ct)
    {
        await using var provider = compose(context);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request, ct);
    }
    private static long Version(TenantSeedDto? row) => row?.Version ?? throw new InvalidOperationException("Seed tenant is absent.");
    private static long Version(CourseSeedDto? row) => row?.Version ?? throw new InvalidOperationException("Seed course is absent.");
    private static long Version(LessonSeedDto? row) => row?.Version ?? throw new InvalidOperationException("Seed lesson is absent.");
    private static bool IsRace(Error error) => error.Code == "concurrency_conflict"
        || error.Code == "business_rule_violation" && error.Details is { } details
            && details.Values.SelectMany(reasons => reasons).Any(reason => RaceReasons.Contains(reason.Key));
    private static readonly HashSet<string> RaceReasons = new(StringComparer.Ordinal)
    {
        "lockey_slug_taken", "lockey_identifier_taken", "lockey_host_taken", "lockey_schema_version_taken",
        "lockey_customization_key_already_live", "lockey_customization_not_a_draft", "lockey_locale_taken", "lockey_setting_taken",
        "lockey_education_locale_already_exists", "lockey_education_translation_requires_draft", "lockey_education_publish_requires_draft",
    };
}

public static partial class SeedRunnerLog
{
    [LoggerMessage(EventId = 7002, Level = LogLevel.Information, Message = "Seeded {What} for {Slug}.")]
    public static partial void Seeded(ILogger logger, string what, string slug);
    [LoggerMessage(EventId = 7003, Level = LogLevel.Information, Message = "{What} for {Slug} already present; leaving it alone.")]
    public static partial void AlreadyPresent(ILogger logger, string what, string slug);
}

/// <summary>Trusted non-request execution, with the exact nullable organization of each act.</summary>
public sealed class SeedTenantContext(TenantId tenantId, OrganizationId? organizationId) : ITenantContext
{
    public bool IsResolved => true;
    public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
    public TenantId TenantId => tenantId;
    public OrganizationId? OrganizationId => organizationId;
    public UserId? UserId => null;
    public string? CorrelationId => null;
    public string? ModuleName => null;
}
