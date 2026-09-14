using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Real application-role EF graphs, root concurrency and contained audit capture.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class EducationPersistenceTests(SchemaFixture schema)
{
    private static readonly FixedClock Clock = new(new DateTimeOffset(2026, 9, 14, 12, 0, 0, TimeSpan.Zero));
    private static readonly FixedClock Later = new(Clock.UtcNow.AddHours(1));
    private static readonly UserId Actor = UserId.From(SchemaFixture.Actor);
    private static readonly CourseId CourseId = CourseId.From(Guid.Parse("c7777777-1111-7111-8111-111111111111"));
    private static readonly LessonId LessonId = LessonId.From(Guid.Parse("d7777777-1111-7111-8111-111111111111"));

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CompositionRoot_ResolvesEducationOnTheOwningConnectionAndTransaction(bool seeder, bool organizationScoped)
    {
        await using var dataSource = NpgsqlDataSource.Create(schema.Postgres.AppConnectionString);
        await using var provider = BuildProvider(dataSource, seeder, organizationScoped);
        await using var operation = await Operation.OpenAsync(provider);

        operation.Frame.IsOwner.Should().BeTrue();
        operation.Context.Database.GetDbConnection().Should().BeSameAs(operation.UnitOfWork.Connection);
        operation.Context.Database.CurrentTransaction.Should().NotBeNull();
        operation.Context.Database.CurrentTransaction!.GetDbTransaction().Should().BeSameAs(operation.UnitOfWork.Transaction);
        var tenancy = operation.Services.GetRequiredService<TenancyDbContext>();
        tenancy.Database.GetDbConnection().Should().BeSameAs(operation.UnitOfWork.Connection);
        tenancy.Database.CurrentTransaction!.GetDbTransaction().Should().BeSameAs(operation.UnitOfWork.Transaction);

        await using var who = operation.UnitOfWork.Connection.CreateCommand();
        who.Transaction = operation.UnitOfWork.Transaction;
        who.CommandText = "SELECT current_user";
        (await who.ExecuteScalarAsync()).Should().Be("learnstack_app");

        var scoped = EducationSchemaSeed.Find(SchemaFixture.TenantA, organizationScoped ? SchemaFixture.OrgA1 : null);
        var courseId = LearnStack.Modules.Education.Domain.CourseId.From(scoped.CourseId);
        var course = await operation.Context.Courses.Include(course => course.Translations)
            .SingleAsync(course => course.Id == courseId);
        course.Translations.Should().ContainSingle();
        course.OrganizationId.Should().Be(organizationScoped ? OrganizationId.From(SchemaFixture.OrgA1) : null);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PersistedGraphs_RoundTripPinsTranslationsAndContainedAuditWhileEachWriteTouchesOneRoot(bool organizationScoped)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = BuildProvider(dataSource, seeder: false, organizationScoped);
        var organization = organizationScoped ? OrganizationId.From(SchemaFixture.OrgA1) : (OrganizationId?)null;

        await using (var creating = await Operation.OpenAsync(provider))
        {
            var course = Course.Create(CourseId, TenantId.From(SchemaFixture.TenantA), organization,
                "persisted-course", Clock, Actor, "difficulty", 7, "intro");
            course.AddTranslation("EN-us", "Course 日本語", null, "course-en", Clock, Actor).IsSuccess.Should().BeTrue();
            creating.Context.Courses.Add(course);
            await creating.Context.SaveChangesAsync();
            AssertCreatedCapture(creating.Capture, nameof(Course), CourseId.Value, "en-US", "Course 日本語");
            await creating.Frame.CompleteAsync();
        }

        const string body = """{ "text": "Ders 🎼", "count": 3, "nested": { "enabled": true } }""";
        await using (var creating = await Operation.OpenAsync(provider))
        {
            // A persisted course, not an all-new graph: parent scope derivation and
            // relationship fixup must work when the parent was loaded earlier.
            var course = await creating.Context.Courses.SingleAsync(course => course.Id == CourseId);
            var lesson = Lesson.Create(LessonId, course, 4, "content-shape", 9, Clock, Actor);
            lesson.AddTranslation("TR-tr", "Ders 🎼", "lesson-tr", body, Clock, Actor).IsSuccess.Should().BeTrue();
            creating.Context.Lessons.Add(lesson);
            await creating.Context.SaveChangesAsync();
            course.Version.Should().Be(1);
            creating.Context.Entry(course).State.Should().Be(EntityState.Unchanged);
            AssertCreatedCapture(creating.Capture, nameof(Lesson), LessonId.Value, "tr-TR", "Ders 🎼");
            await creating.Frame.CompleteAsync();
        }

        await using (var updating = await Operation.OpenAsync(provider))
        {
            var course = await updating.Context.Courses.Include(course => course.Translations)
                .SingleAsync(course => course.Id == CourseId);
            course.Version.Should().Be(1);
            course.AddTranslation("fr", "Cours", "Résumé", "course-fr", Later, Actor).IsSuccess.Should().BeTrue();
            await updating.Context.SaveChangesAsync();
            course.Version.Should().Be(2);
            AssertAddedTranslationCapture(updating.Capture, nameof(Course), CourseId.Value, "fr");
            await updating.Frame.CompleteAsync();
        }

        await using (var updating = await Operation.OpenAsync(provider))
        {
            var lesson = await updating.Context.Lessons.Include(lesson => lesson.Translations)
                .SingleAsync(lesson => lesson.Id == LessonId);
            lesson.Version.Should().Be(1);
            lesson.AddTranslation("fr", "Leçon", "lesson-fr", "{\"text\":\"Bonjour\"}", Later, Actor).IsSuccess.Should().BeTrue();
            await updating.Context.SaveChangesAsync();
            lesson.Version.Should().Be(2);
            AssertAddedTranslationCapture(updating.Capture, nameof(Lesson), LessonId.Value, "fr");
            await updating.Frame.CompleteAsync();
        }

        await using (var publishing = await Operation.OpenAsync(provider))
        {
            var course = await publishing.Context.Courses.SingleAsync(course => course.Id == CourseId);
            course.Publish(Later, Actor).IsSuccess.Should().BeTrue();
            await publishing.Context.SaveChangesAsync();
            publishing.Capture.Changes.Should().ContainSingle().Which.EntityType.Should().Be(nameof(Course));
            await publishing.Frame.CompleteAsync();
        }

        await using (var publishing = await Operation.OpenAsync(provider))
        {
            var lesson = await publishing.Context.Lessons.SingleAsync(lesson => lesson.Id == LessonId);
            lesson.Publish(Later, Actor).IsSuccess.Should().BeTrue();
            await publishing.Context.SaveChangesAsync();
            publishing.Capture.Changes.Should().ContainSingle().Which.EntityType.Should().Be(nameof(Lesson));
            await publishing.Frame.CompleteAsync();
        }

        await using var reading = await Operation.OpenAsync(provider);
        var storedCourse = await reading.Context.Courses.Include(course => course.Translations)
            .SingleAsync(course => course.Id == CourseId);
        var storedLesson = await reading.Context.Lessons.Include(lesson => lesson.Translations)
            .SingleAsync(lesson => lesson.Id == LessonId);
        storedCourse.TenantId.Should().Be(TenantId.From(SchemaFixture.TenantA));
        storedCourse.OrganizationId.Should().Be(organization);
        storedCourse.SlugKey.Should().Be("persisted-course");
        storedCourse.LevelTaxonomyKey.Should().Be("difficulty");
        storedCourse.LevelTaxonomySchemaVersion.Should().Be(7);
        storedCourse.LevelBandKey.Should().Be("intro");
        storedCourse.Version.Should().Be(3);
        storedCourse.Status.Should().Be(PublicationStatus.Published);
        storedCourse.CreatedAt.Should().Be(Clock.UtcNow);
        storedCourse.UpdatedAt.Should().Be(Later.UtcNow);
        storedCourse.Translations.Select(translation => translation.Locale).Should().BeEquivalentTo("en-US", "fr");
        storedCourse.Translations.Should().OnlyContain(translation => translation.TenantId == storedCourse.TenantId && translation.OrganizationId == organization);
        storedCourse.Translations.Single(translation => translation.Locale == "en-US").Summary.Should().BeNull();
        storedCourse.Translations.Single(translation => translation.Locale == "fr").Summary.Should().Be("Résumé");
        storedLesson.CourseId.Should().Be(CourseId);
        storedLesson.OrganizationId.Should().Be(organization);
        storedLesson.Sort.Should().Be(4);
        storedLesson.ContentTypeKey.Should().Be("content-shape");
        storedLesson.ContentTypeSchemaVersion.Should().Be(9);
        storedLesson.Status.Should().Be(PublicationStatus.Published);
        storedLesson.Version.Should().Be(3);
        storedLesson.Translations.Select(translation => translation.Locale).Should().BeEquivalentTo("tr-TR", "fr");
        storedLesson.Translations.Should().OnlyContain(translation => translation.TenantId == storedCourse.TenantId && translation.OrganizationId == organization);
        using var actualBody = JsonDocument.Parse(storedLesson.Translations.Single(translation => translation.Locale == "tr-TR").Body);
        using var expectedBody = JsonDocument.Parse(body);
        JsonElement.DeepEquals(actualBody.RootElement, expectedBody.RootElement).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddingDifferentLocales_CompetesOnRootVersionAndLosingWriteLeavesNoSatellite(bool lessonRoot)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var dataSource = NpgsqlDataSource.Create(database.AppConnectionString);
        await using var provider = BuildProvider(dataSource, seeder: true, organizationScoped: false);
        await CreateRootsAsync(provider, lessonRoot);

        await using var winner = await Operation.OpenAsync(provider);
        await using var loser = await Operation.OpenAsync(provider);
        winner.UnitOfWork.Connection.Should().NotBeSameAs(loser.UnitOfWork.Connection);
        if (lessonRoot)
        {
            var first = await winner.Context.Lessons.Include(lesson => lesson.Translations).SingleAsync();
            var second = await loser.Context.Lessons.Include(lesson => lesson.Translations).SingleAsync();
            first.Version.Should().Be(0);
            second.Version.Should().Be(first.Version);
            first.AddTranslation("en", "Winner", "winner", "{}", Clock, Actor).IsSuccess.Should().BeTrue();
            second.AddTranslation("tr", "Loser", "loser", "{}", Clock, Actor).IsSuccess.Should().BeTrue();
        }
        else
        {
            var first = await winner.Context.Courses.Include(course => course.Translations).SingleAsync();
            var second = await loser.Context.Courses.Include(course => course.Translations).SingleAsync();
            first.Version.Should().Be(0);
            second.Version.Should().Be(first.Version);
            first.AddTranslation("en", "Winner", null, "winner", Clock, Actor).IsSuccess.Should().BeTrue();
            second.AddTranslation("tr", "Loser", null, "loser", Clock, Actor).IsSuccess.Should().BeTrue();
        }

        await winner.Context.SaveChangesAsync();
        await winner.Frame.CompleteAsync();
        var saveLoser = () => loser.Context.SaveChangesAsync();
        await saveLoser.Should().ThrowAsync<DbUpdateConcurrencyException>();
        await loser.Frame.FailAsync();

        await using var reading = await Operation.OpenAsync(provider);
        if (lessonRoot)
        {
            var stored = await reading.Context.Lessons.Include(lesson => lesson.Translations).SingleAsync();
            stored.Version.Should().Be(1);
            stored.Translations.Should().ContainSingle().Which.Locale.Should().Be("en");
            (await reading.Context.Courses.SingleAsync()).Version.Should().Be(0);
        }
        else
        {
            var stored = await reading.Context.Courses.Include(course => course.Translations).SingleAsync();
            stored.Version.Should().Be(1);
            stored.Translations.Should().ContainSingle().Which.Locale.Should().Be("en");
        }
    }

    private static async Task CreateRootsAsync(ServiceProvider provider, bool lessonRoot)
    {
        await using (var creating = await Operation.OpenAsync(provider))
        {
            creating.Context.Courses.Add(Course.Create(CourseId, TenantId.From(SchemaFixture.TenantA), null,
                "concurrency", Clock, Actor));
            await creating.Context.SaveChangesAsync();
            await creating.Frame.CompleteAsync();
        }

        if (lessonRoot)
        {
            await using var creating = await Operation.OpenAsync(provider);
            var course = await creating.Context.Courses.SingleAsync();
            creating.Context.Lessons.Add(Lesson.Create(LessonId, course, 0, "content", 1, Clock, Actor));
            await creating.Context.SaveChangesAsync();
            await creating.Frame.CompleteAsync();
        }
    }

    private static void AssertCreatedCapture(IAuditStateCapture capture, string entityType, Guid id, string locale, string title)
    {
        var change = capture.Changes.Should().ContainSingle("the satellite is contained in its root's audit subject").Subject;
        change.EntityType.Should().Be(entityType);
        change.EntityId.Should().Be(id.ToString());
        change.BeforeJson.Should().BeNull();
        change.AfterJson.Should().NotBeNull();
        // A created root has known membership, so the actual snapshot includes its
        // natural-key member. No independent surrogate identifier is required.
        using var after = JsonDocument.Parse(change.AfterJson!);
        after.RootElement.GetProperty("Translations").GetProperty(locale).GetProperty("Title").GetString().Should().Be(title);
        change.Fields.Should().Contain(field => field.Path == $"/{entityType}/{id}/Translations/{locale}/Title");
    }

    private static void AssertAddedTranslationCapture(IAuditStateCapture capture, string entityType, Guid id, string locale)
    {
        var change = capture.Changes.Should().ContainSingle().Subject;
        change.EntityType.Should().Be(entityType);
        change.EntityId.Should().Be(id.ToString());
        change.BeforeJson.Should().NotBeNull();
        change.AfterJson.Should().NotBeNull();
        change.Fields.Should().Contain(field => field.Path == $"/{entityType}/{id}/Translations/{locale}/Title" && field.BeforeJson == null);
        change.Fields.Should().NotContain(field => field.Path.EndsWith("/Version", StringComparison.Ordinal));
    }

    private static ServiceProvider BuildProvider(NpgsqlDataSource dataSource, bool seeder, bool organizationScoped)
    {
        var tenant = new EducationTenant(organizationScoped);
        if (seeder)
        {
            return SeedComposition.Build(dataSource, tenant, NullLoggerFactory.Instance);
        }

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(dataSource);
        services.AddSingleton<ITenantContextAccessor>(new StaticTenantContextAccessor(tenant));
        services.AddTransient<ITenantContext>(provider =>
            provider.GetRequiredService<ITenantContextAccessor>().Current ?? UnresolvedTenantContext.Instance);
        services.AddLearnStackPersistence(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["ConnectionStrings:Default"] = dataSource.ConnectionString }).Build());
        return services.BuildServiceProvider();
    }

    private sealed class EducationTenant(bool organizationScoped) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => TenantId.From(SchemaFixture.TenantA);
        public OrganizationId? OrganizationId => organizationScoped
            ? LearnStack.SharedKernel.Identifiers.OrganizationId.From(SchemaFixture.OrgA1) : null;
        public UserId? UserId => Actor;
        public TenantContextOrigin? Origin => TenantContextOrigin.HostAndClaim;
        public string? CorrelationId => null;
        public string? ModuleName => "education";
    }

    private sealed class Operation : IAsyncDisposable
    {
        private readonly AsyncServiceScope _scope;
        private Operation(AsyncServiceScope scope, IUnitOfWork unitOfWork, IUnitOfWorkScope frame)
        {
            _scope = scope;
            UnitOfWork = unitOfWork;
            Frame = frame;
            Context = Services.GetRequiredService<EducationDbContext>();
            Capture = Services.GetRequiredService<IAuditStateCapture>();
        }

        public IServiceProvider Services => _scope.ServiceProvider;
        public IUnitOfWork UnitOfWork { get; }
        public IUnitOfWorkScope Frame { get; }
        public EducationDbContext Context { get; }
        public IAuditStateCapture Capture { get; }

        public static async Task<Operation> OpenAsync(ServiceProvider provider)
        {
            var scope = provider.CreateAsyncScope();
            try
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var frame = await unitOfWork.BeginTransactionAsync();
                await unitOfWork.SetTenantContextAsync(scope.ServiceProvider.GetRequiredService<ITenantContext>());
                return new Operation(scope, unitOfWork, frame);
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Frame.DisposeAsync();
            await _scope.DisposeAsync();
        }
    }
}
