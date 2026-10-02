using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Validation;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class EducationWriterTests(SchemaFixture schema, WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string StringSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"text":{"type":"string"}},"required":["text"],"additionalProperties":false}""";
    private const string NumberSchema = """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"text":{"type":"number"}},"required":["text"],"additionalProperties":false}""";
    private static readonly string[] ExpectedOperations = ["education.course.create", "education.course.translation_add", "education.course.publish", "education.lesson.create", "education.lesson.translation_add", "education.lesson.publish"];
    private const string Body = """{"text":"A lesson"}""";

    [Fact]
    public async Task Composed_writer_bounds_instances_before_lookup_and_preserves_state_on_oversize()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await TypeAsync(source, context, 1, StringSchema);
        var course = await CourseAsync(source, context, "bounded");
        var lesson = await LessonAsync(source, context, course.Id);
        var before = await LessonStateAsync(source, context, lesson.Id);
        var auditCount = await SuccessfulAuditsAsync(source, context, "education.lesson.translation_add");
        var body = "{\"text\":\"" + new string('a', JsonInstanceLimits.MaxBytes - 11) + "\"}";
        System.Text.Encoding.UTF8.GetByteCount(body).Should().Be(JsonInstanceLimits.MaxBytes);
        var command = new AddLessonTranslationCommand(lesson.Id, lesson.Version, "en", "Bounded", "bounded", body);
        foreach (var target in new[] { lesson.Id, Guid.CreateVersion7() })
        {
            var refused = await SendAsync(source, context, command with { LessonId = target, Body = body + " " });
            refused.Error!.Code.Should().Be("validation_failed");
            refused.Error.Details.Should().ContainKey("Body", "size admission runs before even a missing-root lookup");
        }
        (await LessonStateAsync(source, context, lesson.Id)).Should().BeEquivalentTo(before);
        (await SuccessfulAuditsAsync(source, context, "education.lesson.translation_add")).Should().Be(auditCount);
        (await SendAsync(source, context, command)).IsSuccess.Should().BeTrue("the inclusive instance boundary must still pass the composed schema writer");
    }

    [Fact]
    public async Task Six_commands_preserve_independent_roots_explicit_policy_and_contained_audit_subjects()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await TypeAsync(source, context, 1, StringSchema);
        var course = await CourseAsync(source, context, "course", "enrollment_required");
        var translated = await SendAsync(source, context, new AddCourseTranslationCommand(course.Id, course.Version, "EN", "Course", "Summary", "translated-course"));
        translated.IsSuccess.Should().BeTrue();
        var parentBefore = await CourseStateAsync(source, context, course.Id);
        var lesson = await LessonAsync(source, context, course.Id);
        var lessonTranslation = await SendAsync(source, context, new AddLessonTranslationCommand(lesson.Id, lesson.Version, "EN", "Lesson", "translated-lesson", Body));
        lessonTranslation.IsSuccess.Should().BeTrue();
        var publishedLesson = await SendAsync(source, context, new PublishLessonCommand(lesson.Id, lessonTranslation.Value!.Version));
        publishedLesson.Value!.Status.Should().Be("Published");
        (await CourseStateAsync(source, context, course.Id)).Should().BeEquivalentTo(parentBefore);
        var publishedCourse = await SendAsync(source, context, new PublishCourseCommand(course.Id, translated.Value!.Version));
        publishedCourse.Value!.ContentAccess.Should().Be("enrollment_required");
        (await LessonStateAsync(source, context, lesson.Id)).Version.Should().Be(publishedLesson.Value.Version);
        var courseFinal = await CourseStateAsync(source, context, course.Id);
        courseFinal.Translations.Should().ContainSingle().Which.Locale.Should().Be("en");
        var refused = await SendAsync(source, context, new AddCourseTranslationCommand(course.Id, courseFinal.Version, "en", "Changed", null, "changed"));
        refused.Error!.Code.Should().Be("business_rule_violation");
        (await CourseStateAsync(source, context, course.Id)).Should().BeEquivalentTo(courseFinal);
        await AssertAuditAsync(source, context, course.Id, lesson.Id);
    }

    [Fact]
    public async Task Exact_active_bindings_and_existing_deprecated_pins_use_their_own_schema()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var taxonomyId = Guid.CreateVersion7();
        (await SendAsync(source, context, new RegisterTenantLevelTaxonomyCommand(taxonomyId, "levels", 1,
            new Dictionary<string, string> { ["en"] = "Levels" }, [new TaxonomyItemInput("basic", new Dictionary<string, string> { ["en"] = "Basic" }, 0)]))).IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new PublishTenantLevelTaxonomyCommand(taxonomyId))).IsSuccess.Should().BeTrue();
        var invalidBand = await SendAsync(source, context, new CreateCourseCommand(Guid.CreateVersion7(), "wrong-band", "public", "levels", 1, "absent"));
        invalidBand.Error!.Code.Should().Be("validation_failed");
        var missingRevision = await SendAsync(source, context, new CreateCourseCommand(Guid.CreateVersion7(), "wrong-revision", "public", "levels", 2, "basic"));
        missingRevision.Error!.Code.Should().Be("validation_failed");
        var course = await SendAsync(source, context, new CreateCourseCommand(Guid.CreateVersion7(), "pinned", "public", "levels", 1, "basic"));
        course.IsSuccess.Should().BeTrue();
        await TypeAsync(source, context, 1, StringSchema);
        var oldLesson = await LessonAsync(source, context, course.Value!.Id);
        var oldBefore = await LessonStateAsync(source, context, oldLesson.Id);
        (await SendAsync(source, context, new AddLessonTranslationCommand(oldLesson.Id, 0, "fr", "Absent", "absent", Body)))
            .Error!.Code.Should().Be("validation_failed");
        (await LessonStateAsync(source, context, oldLesson.Id)).Should().BeEquivalentTo(oldBefore);
        await TypeAsync(source, context, 2, NumberSchema);
        var refusedNew = await SendAsync(source, context, new CreateLessonCommand(Guid.CreateVersion7(), course.Value.Id, 1, "shape", 1));
        refusedNew.Error!.Code.Should().Be("validation_failed");
        var oldBody = await SendAsync(source, context, new AddLessonTranslationCommand(oldLesson.Id, 0, "en", "Old", "old", Body));
        oldBody.IsSuccess.Should().BeTrue("an existing pin remains eligible after deprecation and never picks v2");
        var oldAfter = await LessonStateAsync(source, context, oldLesson.Id);
        (await SendAsync(source, context, new PublishLessonCommand(oldLesson.Id, 0))).Error!.Code.Should().Be("concurrency_conflict");
        (await SendAsync(source, context, new AddLessonTranslationCommand(oldLesson.Id, oldAfter.Version, "EN", "Duplicate", "duplicate", Body)))
            .Error!.Code.Should().Be("business_rule_violation");
        (await LessonStateAsync(source, context, oldLesson.Id)).Should().BeEquivalentTo(oldAfter);
        var newLesson = await LessonAsync(source, context, course.Value.Id, version: 2);
        var before = await LessonStateAsync(source, context, newLesson.Id);
        var wrongBody = await SendAsync(source, context, new AddLessonTranslationCommand(newLesson.Id, before.Version, "en", "New", "new", Body));
        wrongBody.Error!.Code.Should().Be("validation_failed");
        wrongBody.Error.Details.Should().ContainKey("/text");
        wrongBody.Error.Details!.Count.Should().BeLessThanOrEqualTo(25);
        (await LessonStateAsync(source, context, newLesson.Id)).Should().BeEquivalentTo(before);
        var numberBody = await SendAsync(source, context, new AddLessonTranslationCommand(newLesson.Id, before.Version, "en", "New", "new", """{"text":7}"""));
        numberBody.IsSuccess.Should().BeTrue("writers admit all approved schemas, independently of the text-card rendering subset");
    }

    [Fact]
    public async Task Hidden_roots_are_not_found_and_visible_incompatible_scope_is_refused_before_mutation()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var foreign = await ProvisionAsync(source);
        await TypeAsync(source, context, 1, StringSchema);
        var wide = await CourseAsync(source, context, "wide");
        var orgA = context with { Organization = context.FirstOrganization };
        var orgB = context with { Organization = context.SecondOrganization };
        var scoped = await CourseAsync(source, orgA, "scoped");
        var foreignCourse = await CourseAsync(source, foreign, "foreign");
        (await SendAsync(source, orgA, new CreateLessonCommand(Guid.CreateVersion7(), wide.Id, 0, "shape", 1)))
            .Error!.Code.Should().Be("resource_scope_violation");
        (await SendAsync(source, orgA, new PublishCourseCommand(wide.Id, wide.Version))).Error!.Code.Should().Be("resource_scope_violation");
        (await SendAsync(source, orgA, new AddCourseTranslationCommand(wide.Id, wide.Version, "en", "Wide", null, "wide")))
            .Error!.Code.Should().Be("resource_scope_violation");
        var wideLesson = await LessonAsync(source, context, wide.Id);
        var wideLessonBefore = await LessonStateAsync(source, context, wideLesson.Id);
        (await SendAsync(source, orgA, new AddLessonTranslationCommand(wideLesson.Id, wideLesson.Version, "en", "Wide", "wide", Body)))
            .Error!.Code.Should().Be("resource_scope_violation");
        (await SendAsync(source, orgA, new PublishLessonCommand(wideLesson.Id, wideLesson.Version)))
            .Error!.Code.Should().Be("resource_scope_violation");
        foreach (var hidden in new[] { scoped.Id, foreignCourse.Id, Guid.CreateVersion7() })
        {
            (await SendAsync(source, orgB, new CreateLessonCommand(Guid.CreateVersion7(), hidden, 0, "shape", 1)))
                .Error!.Code.Should().Be("not_found");
            (await SendAsync(source, orgB, new PublishCourseCommand(hidden, 0))).Error!.Code.Should().Be("not_found");
            (await SendAsync(source, orgB, new AddCourseTranslationCommand(hidden, 0, "en", "Hidden", null, "hidden")))
                .Error!.Code.Should().Be("not_found");
        }
        (await SendAsync(source, context, new PublishCourseCommand(scoped.Id, 0))).Error!.Code.Should().Be("not_found");
        var child = await LessonAsync(source, orgA, scoped.Id);
        var state = await LessonStateAsync(source, orgA, child.Id);
        state.OrganizationId.Should().Be(orgA.OrganizationId);
        state.TenantId.Should().Be(context.TenantId);
        (await SendAsync(source, orgB, new PublishLessonCommand(child.Id, 0))).Error!.Code.Should().Be("not_found");
        (await SendAsync(source, context, new PublishLessonCommand(child.Id, 0))).Error!.Code.Should().Be("not_found");
        (await SendAsync(source, orgB, new AddLessonTranslationCommand(child.Id, 0, "en", "Hidden", "hidden", Body))).Error!.Code.Should().Be("not_found");
        (await SendAsync(source, context, new AddLessonTranslationCommand(child.Id, 0, "en", "Scoped", "scoped", Body)))
            .Error!.Code.Should().Be("not_found");
        (await CourseStateAsync(source, context, wide.Id)).Version.Should().Be(0);
        (await CourseStateAsync(source, orgA, scoped.Id)).Version.Should().Be(0);
        (await LessonStateAsync(source, context, wideLesson.Id)).Should().BeEquivalentTo(wideLessonBefore);
        (await LessonStateAsync(source, orgA, child.Id)).Should().BeEquivalentTo(state);
    }

    [Fact]
    public async Task Locale_slug_lifecycle_and_stale_refusals_preserve_the_root_and_reserve_slugs_on_insert()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var foreign = await ProvisionAsync(source);
        var tenant = (await SendAsync(source, context, new GetTenantSeedStateQuery())).Value!.State!;
        (await SendAsync(source, context, new AddTenantLocaleCommand(tenant.Version, "fr", false, false, 1))).IsSuccess.Should().BeTrue();
        var first = await CourseAsync(source, context, "first");
        var second = await CourseAsync(source, context, "second");
        var before = await CourseStateAsync(source, context, first.Id);
        (await SendAsync(source, context, new AddCourseTranslationCommand(first.Id, 0, "fr", "Disabled", null, "disabled")))
            .Error!.Code.Should().Be("validation_failed");
        (await SendAsync(source, context, new AddCourseTranslationCommand(first.Id, 0, "de", "Absent", null, "absent")))
            .Error!.Code.Should().Be("validation_failed");
        (await CourseStateAsync(source, context, first.Id)).Should().BeEquivalentTo(before);
        var translated = await SendAsync(source, context, new AddCourseTranslationCommand(first.Id, 0, "en", "Title", null, "reserved"));
        translated.IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new AddCourseTranslationCommand(second.Id, 0, "en", "Collision", null, "reserved")))
            .Error!.Code.Should().Be("business_rule_violation");
        (await CourseStateAsync(source, context, second.Id)).Version.Should().Be(0);
        (await SendAsync(source, context, new CreateCourseCommand(Guid.CreateVersion7(), "first", "public")))
            .Error!.Code.Should().Be("business_rule_violation");
        var after = await CourseStateAsync(source, context, first.Id);
        (await SendAsync(source, context, new PublishCourseCommand(first.Id, 0))).Error!.Code.Should().Be("concurrency_conflict");
        (await SendAsync(source, context, new AddCourseTranslationCommand(first.Id, after.Version, "EN", "Duplicate", null, "other")))
            .Error!.Code.Should().Be("business_rule_violation");
        (await CourseStateAsync(source, context, first.Id)).Should().BeEquivalentTo(after);
        var foreignRoot = await CourseAsync(source, foreign, "first");
        (await SendAsync(source, foreign, new AddCourseTranslationCommand(foreignRoot.Id, 0, "en", "Foreign", null, "reserved")))
            .IsSuccess.Should().BeTrue("slug uniqueness is tenant-local");
        (await SuccessfulAuditsAsync(source, context, "education.course.translation_add")).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_requires_durable_must_audit_and_rolls_back_on_audit_failure(bool lessonPublication)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await TypeAsync(source, context, 1, StringSchema);
        var course = await CourseAsync(source, context, "course");
        var lesson = await LessonAsync(source, context, course.Id);
        await OwnerAsync(database, "REVOKE INSERT ON audit_log FROM learnstack_app");
        Func<Task> publish = lessonPublication
            ? async () => { await SendAsync(source, context, new PublishLessonCommand(lesson.Id, 0)); }
        : async () => { await SendAsync(source, context, new PublishCourseCommand(course.Id, 0)); };
        (await publish.Should().ThrowAsync<AuditWriteFailedException>()).Which.Error.Code.Should().Be("audit_unavailable");
        (await CourseStateAsync(source, context, course.Id)).Status.Should().Be("Draft");
        (await CourseStateAsync(source, context, course.Id)).Version.Should().Be(0);
        (await LessonStateAsync(source, context, lesson.Id)).Status.Should().Be("Draft");
        (await LessonStateAsync(source, context, lesson.Id)).Version.Should().Be(0);
        (await SuccessfulAuditsAsync(source, context, lessonPublication ? "education.lesson.publish" : "education.course.publish")).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Slug_diagnostics_identify_only_visible_conflicting_roots(bool lessonTranslation)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await TypeAsync(source, context, 1, StringSchema);
        var orgA = context with { Organization = context.FirstOrganization };
        var orgB = context with { Organization = context.SecondOrganization };
        var courseA = await CourseAsync(source, orgA, "a");
        var courseB = await CourseAsync(source, orgB, "b");
        var courseC = await CourseAsync(source, orgA, "c");
        var first = lessonTranslation ? (await LessonAsync(source, orgB, courseB.Id)).Id : courseB.Id;
        var target = lessonTranslation ? (await LessonAsync(source, orgA, courseA.Id)).Id : courseA.Id;
        var visibleTarget = lessonTranslation ? (await LessonAsync(source, orgA, courseC.Id)).Id : courseC.Id;
        async Task<Error?> Translate(Context owner, Guid id, string slug)
        {
            if (lessonTranslation)
                return (await SendAsync(source, owner, new AddLessonTranslationCommand(id, 0, "EN", "Title", slug, Body))).Error;
            return (await SendAsync(source, owner, new AddCourseTranslationCommand(id, 0, "EN", "Title", null, slug))).Error;
        }
        (await Translate(orgB, first, "hidden-slug")).Should().BeNull();
        var hidden = (await Translate(orgA, target, "hidden-slug"))!;
        hidden.Code.Should().Be("business_rule_violation");
        hidden.Details!["Slug"].Single().Params.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["locale"] = "en",
            ["slug"] = "hidden-slug",
        });
        JsonSerializer.Serialize(hidden).Should().NotContain(first.ToString());
        (await Translate(orgA, target, "visible-slug")).Should().BeNull();
        var visible = (await Translate(orgA, visibleTarget, "visible-slug"))!;
        visible.Details!["Slug"].Single().Params!["entityId"].Should().Be(target.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_education_unique_constraints_remain_infrastructure_faults(bool lessonCreation)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await TypeAsync(source, context, 1, StringSchema);
        var course = await CourseAsync(source, context, "first");
        if (lessonCreation) await LessonAsync(source, context, course.Id);
        await OwnerAsync(database, lessonCreation
            ? "CREATE UNIQUE INDEX ux_test_unowned_education ON lessons(sort)"
            : "CREATE UNIQUE INDEX ux_test_unowned_education ON courses(content_access)");
        var id = Guid.CreateVersion7();
        Func<Task> write = lessonCreation
            ? async () => { await SendAsync(source, context, new CreateLessonCommand(id, course.Id, 0, "shape", 1)); }
        : async () => { await SendAsync(source, context, new CreateCourseCommand(id, "second", "public")); };
        var fault = (await write.Should().ThrowAsync<DbUpdateException>()).Which;
        fault.InnerException.Should().BeOfType<PostgresException>().Which.ConstraintName.Should().Be("ux_test_unowned_education");
        var problem = LearnStack.Api.Common.ProblemDetailsFactory.For(fault);
        problem.Status.Should().Be(500);
        problem.Extensions["code"].Should().Be("internal_error");
        if (lessonCreation) (await SendAsync(source, context, new GetLessonSeedStateQuery(id))).Value!.State.Should().BeNull();
        else (await SendAsync(source, context, new GetCourseSeedStateQuery(id))).Value!.State.Should().BeNull();
    }

    [Fact]
    public async Task Concurrent_publication_at_one_version_has_one_winner_and_one_must_success_audit()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var course = await CourseAsync(source, context, "course");
        var barrier = new ReadBarrier();
        await using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services => services.AddScoped<ICourseWriteStore>(provider =>
                new BarrierCourseStore(new CourseWriteStore(provider.GetRequiredService<EducationDbContext>()), barrier))));
        async Task<Result<CourseWriteDto>> Publish()
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new PublishCourseCommand(course.Id, 0));
        }
        var results = await Task.WhenAll(Publish(), Publish());
        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result => result.IsFailure).Which.Error!.Code.Should().Be("concurrency_conflict");
        var state = await CourseStateAsync(source, context, course.Id);
        state.Status.Should().Be("Published");
        state.Version.Should().Be(1);
        (await SuccessfulAuditsAsync(source, context, "education.course.publish")).Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_translation_inserts_reserve_one_tenant_slug_and_roll_back_the_loser_stamp()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var first = await CourseAsync(source, context, "first");
        var second = await CourseAsync(source, context, "second");
        var barrier = new ReadBarrier();
        await using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services => services.AddScoped<ICourseWriteStore>(provider =>
                new BarrierCourseStore(new CourseWriteStore(provider.GetRequiredService<EducationDbContext>()), barrier))));
        async Task<Result<CourseWriteDto>> Translate(Guid id)
        {
            await using var scope = host.Services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
            return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AddCourseTranslationCommand(id, 0, "en", "Title", null, "reserved"));
        }
        var results = await Task.WhenAll(Translate(first.Id), Translate(second.Id));
        results.Should().ContainSingle(result => result.IsSuccess);
        results.Should().ContainSingle(result => result.IsFailure).Which.Error!.Code.Should().Be("business_rule_violation");
        var states = new[] { await CourseStateAsync(source, context, first.Id), await CourseStateAsync(source, context, second.Id) };
        states.Should().ContainSingle(state => state.Version == 1 && state.Translations.Length == 1);
        states.Should().ContainSingle(state => state.Version == 0 && state.Translations.Length == 0);
        (await SuccessfulAuditsAsync(source, context, "education.course.translation_add")).Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_outer_handler_absorbing_a_post_save_refusal_cannot_commit_the_dirty_or_later_root(bool concurrency)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        var course = await CourseAsync(source, context, "course");
        var later = Guid.CreateVersion7();
        await using var host = factory.WithWebHostBuilder(builder => builder
            .UseSetting("ConnectionStrings:Default", database.AppConnectionString)
            .ConfigureServices(services =>
            {
                services.AddScoped<ICourseWriteStore>(provider => new FailAfterSaveCourseStore(
                    new CourseWriteStore(provider.GetRequiredService<EducationDbContext>()), course.Id, concurrency));
                services.AddTransient<IRequestHandler<AbsorbingOuterCommand, Result<None>>, AbsorbingOuterHandler>();
                services.AddSingleton<IAuditCatalogSource, TestAuditSource>();
            }));
        await using (var scope = host.Services.CreateAsyncScope())
        {
            scope.ServiceProvider.GetRequiredService<ITenantContextAccessor>().Current = context;
            var send = async () => await scope.ServiceProvider.GetRequiredService<ISender>().Send(new AbsorbingOuterCommand(course.Id, later));
            await send.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rollback-only*");
        }
        var final = await CourseStateAsync(source, context, course.Id);
        final.Version.Should().Be(0);
        final.Status.Should().Be("Draft");
        (await SendAsync(source, context, new GetCourseSeedStateQuery(later))).Value!.State.Should().BeNull();
        (await SuccessfulAuditsAsync(source, context, "education.course.publish")).Should().Be(0);
        (await SuccessfulAuditsAsync(source, context, "education.course.create")).Should().Be(1);
    }

    private static async Task<Context> ProvisionAsync(NpgsqlDataSource source)
    {
        var tenant = TenantId.From(Guid.CreateVersion7());
        var first = OrganizationId.From(Guid.CreateVersion7());
        var second = OrganizationId.From(Guid.CreateVersion7());
        (await SendAsync(source, null, new ProvisionTenantCommand(tenant, "writer-" + tenant.Value.ToString("N"), "Writer", first, "first", "First"))).IsSuccess.Should().BeTrue();
        var context = new Context(tenant, first, second);
        (await SendAsync(source, context, new CreateOrganizationCommand(second, "second", "Second"))).IsSuccess.Should().BeTrue();
        var state = (await SendAsync(source, context, new GetTenantSeedStateQuery())).Value!.State!;
        (await SendAsync(source, context, new AddTenantLocaleCommand(state.Version, "en", true, true, 0))).IsSuccess.Should().BeTrue();
        return context;
    }

    private static async Task TypeAsync(NpgsqlDataSource source, Context context, int version, string json)
    {
        var id = Guid.CreateVersion7();
        (await SendAsync(source, context, new RegisterTenantContentTypeCommand(id, "shape", version,
            new Dictionary<string, string> { ["en"] = "Shape" }, json, "default-card"))).IsSuccess.Should().BeTrue();
        (await SendAsync(source, context, new PublishTenantContentTypeCommand(id))).IsSuccess.Should().BeTrue();
    }

    private static async Task<CourseWriteDto> CourseAsync(NpgsqlDataSource source, Context context, string slug, string access = "public")
    {
        var result = await SendAsync(source, context, new CreateCourseCommand(Guid.CreateVersion7(), slug, access));
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    private static async Task<LessonWriteDto> LessonAsync(NpgsqlDataSource source, Context context, Guid course, int version = 1)
    {
        var result = await SendAsync(source, context, new CreateLessonCommand(Guid.CreateVersion7(), course, 0, "shape", version));
        result.IsSuccess.Should().BeTrue();
        return result.Value!;
    }

    private static async Task<Result<T>> SendAsync<T>(NpgsqlDataSource source, Context? context, IRequest<Result<T>> command)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(command);
    }

    private static async Task<CourseSeedDto> CourseStateAsync(NpgsqlDataSource source, Context context, Guid id) =>
        (await SendAsync(source, context, new GetCourseSeedStateQuery(id))).Value!.State!;
    private static async Task<LessonSeedDto> LessonStateAsync(NpgsqlDataSource source, Context context, Guid id) =>
        (await SendAsync(source, context, new GetLessonSeedStateQuery(id))).Value!.State!;

    private static async Task<long> SuccessfulAuditsAsync(NpgsqlDataSource source, Context context, string operation)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM audit_log WHERE operation = @operation AND outcome = 'success'", (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        command.Parameters.AddWithValue("operation", operation);
        var count = (long)(await command.ExecuteScalarAsync())!;
        await frame.FailAsync();
        return count;
    }

    private static async Task AssertAuditAsync(NpgsqlDataSource source, Context context, Guid course, Guid lesson)
    {
        await using var provider = SeedComposition.Build(source, context, NullLoggerFactory.Instance);
        await using var scope = provider.CreateAsyncScope();
        var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetTenantContextAsync(context);
        await using var command = new NpgsqlCommand("""
            SELECT operation, entity_type, entity_id, operation_class, before_state::text, after_state::text, changes::text
            FROM audit_log WHERE operation LIKE 'education.%' AND outcome = 'success'
            """, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        var operations = new List<string>();
        await using (var rows = await command.ExecuteReaderAsync())
        {
            while (await rows.ReadAsync())
            {
                var operation = rows.GetString(0);
                operations.Add(operation);
                var type = operation.Contains(".course.", StringComparison.Ordinal) ? nameof(Course) : nameof(Lesson);
                rows.GetString(1).Should().Be(type);
                rows.GetString(2).Should().Be((type == nameof(Course) ? course : lesson).ToString());
                rows.GetString(3).Should().Be(operation.EndsWith(".publish", StringComparison.Ordinal) ? "Must" : "Should");
                using var after = JsonDocument.Parse(rows.GetString(5));
                after.RootElement.GetProperty("Status").GetString().Should().Be(operation.EndsWith(".publish", StringComparison.Ordinal) ? "published" : "draft");
                if (operation.EndsWith(".publish", StringComparison.Ordinal))
                {
                    using var before = JsonDocument.Parse(rows.GetString(4));
                    before.RootElement.GetProperty("Status").GetString().Should().Be("draft");
                }
                if (operation.EndsWith(".translation_add", StringComparison.Ordinal))
                {
                    rows.GetString(6).Should().Contain("/Translations/en/Title");
                }
            }
        }
        operations.Should().BeEquivalentTo(ExpectedOperations);
        await frame.FailAsync();
    }

    private static async Task OwnerAsync(DisposableSchemaDatabase database, string sql)
    {
        await using var connection = new NpgsqlConnection(database.MigrationConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private sealed record Context(TenantId TenantId, OrganizationId FirstOrganization, OrganizationId SecondOrganization,
        OrganizationId? Organization = null) : ITenantContext
    {
        public bool IsResolved => true;
        public OrganizationId? OrganizationId => Organization;
        public UserId? UserId => null;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => null;
    }
    private sealed class ReadBarrier
    {
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _readers;
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _readers) == 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
    }
    private sealed class BarrierCourseStore(ICourseWriteStore inner, ReadBarrier barrier) : ICourseWriteStore
    {
        public async Task<Course?> FindAsync(CourseId id, CancellationToken cancellationToken)
        {
            var root = await inner.FindAsync(id, cancellationToken);
            await barrier.WaitAsync(cancellationToken);
            return root;
        }
        public Task AddAsync(Course aggregate, CancellationToken cancellationToken = default) => inner.AddAsync(aggregate, cancellationToken);
        public Task UpdateAsync(Course aggregate, CancellationToken cancellationToken = default) => inner.UpdateAsync(aggregate, cancellationToken);
    }
    private sealed class FailAfterSaveCourseStore(ICourseWriteStore inner, Guid failingId, bool concurrency) : ICourseWriteStore
    {
        public Task<Course?> FindAsync(CourseId id, CancellationToken cancellationToken) => inner.FindAsync(id, cancellationToken);
        public Task AddAsync(Course aggregate, CancellationToken cancellationToken = default) => inner.AddAsync(aggregate, cancellationToken);
        public async Task UpdateAsync(Course aggregate, CancellationToken cancellationToken = default)
        {
            await inner.UpdateAsync(aggregate, cancellationToken);
            if (aggregate.Id.Value == failingId)
            {
                if (concurrency) throw new AggregateConcurrencyException("injected post-save refusal");
                throw new AggregateConflictException("injected post-save refusal", "pk_courses");
            }
        }
    }
    private sealed record AbsorbingOuterCommand(Guid FailingId, Guid LaterId) : IRequest<Result<None>>;
    private sealed class AbsorbingOuterHandler(ISender sender) : IRequestHandler<AbsorbingOuterCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(AbsorbingOuterCommand request, CancellationToken cancellationToken)
        {
            var refused = await sender.Send(new PublishCourseCommand(request.FailingId, 0), cancellationToken);
            refused.IsFailure.Should().BeTrue("the refusal is absorbed only after its mutation/save executed");
            var later = await sender.Send(new CreateCourseCommand(request.LaterId, "later", "public"), cancellationToken);
            later.IsSuccess.Should().BeTrue("later work really saved on the shared transaction before the owner rejects commit");
            return Result.Ok(None.Value);
        }
    }
    private sealed class TestAuditSource : IAuditCatalogSource
    {
        public string ModuleName => "test";
        public void Describe(IAuditCatalogBuilder builder) => builder.Off<AbsorbingOuterCommand>();
    }

}
