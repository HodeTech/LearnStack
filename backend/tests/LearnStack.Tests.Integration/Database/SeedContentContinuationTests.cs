using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Education.Application.Contracts.Courses;
using LearnStack.Modules.Education.Application.Contracts.Lessons;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.Tools.Seeder;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Actual fixed content callers, ordinary app-role reads and real DML fence competition.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class SeedContentContinuationTests(SchemaFixture schema) : IAsyncLifetime
{
    private DisposableSchemaDatabase _database = null!;
    public async Task InitializeAsync() => _database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
    public Task DisposeAsync() => _database.DisposeAsync().AsTask();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_later_declared_creation_refuses_without_skipping_its_missing_predecessor(bool lesson)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(_database);
        var predecessor = Describe(lesson ? "create-lesson" : "create-course");
        await PrepareAsync(source, evidence, predecessor);
        var firstCourse = SeedData.English.Curriculum!.Courses[0];
        var laterCourse = SeedData.English.Curriculum.Courses[1];
        var laterLesson = firstCourse.Lessons[1];
        object request = lesson
            ? new CreateLessonCommand(laterLesson.Id, firstCourse.Id, laterLesson.Sort,
                laterLesson.ContentTypeKey, laterLesson.ContentTypeSchemaVersion)
            : new CreateCourseCommand(laterCourse.Id, laterCourse.SlugKey, laterCourse.ContentAccess,
                laterCourse.LevelTaxonomyKey, laterCourse.LevelTaxonomySchemaVersion, laterCourse.LevelBandKey);
        var attempted = predecessor with { Command = request, Target = lesson ? laterLesson.Id : laterCourse.Id };
        var before = await SnapshotAsync(attempted);
        var first = await SnapshotAsync(predecessor);
        var successes = await SuccessesAsync(attempted);

        var refused = await SendAsync(source, evidence, request);

        refused.IsSuccess.Should().BeFalse("a canonical value is not authority to skip the next missing root");
        refused.Error!.Code.Should().Be("concurrency_conflict");
        (await SnapshotAsync(attempted)).Should().Be(before);
        (await SnapshotAsync(predecessor)).Should().Be(first);
        (await SuccessesAsync(attempted)).Should().Be(successes);
        var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance,
            authorizationEvidence: evidence.Source), NullLogger<SeedRunner>.Instance);
        (await runner.RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);
        var installed = await AuditCountAsync(source);
        (await runner.RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);
        (await AuditCountAsync(source)).Should().Be(installed);
    }

    [Fact]
    public async Task Omitting_the_declared_curriculum_cannot_skip_locale_prerequisites_and_the_canonical_run_resumes()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(_database);
        var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance,
            authorizationEvidence: evidence.Source), NullLogger<SeedRunner>.Instance);
        var truncated = () => runner.RunAsync(CancellationToken.None, [SeedData.English with { Curriculum = null }]);
        (await truncated.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*Seed mismatch in built-in content type*");
        var act = Describe("builtin-content");
        (await SnapshotAsync(act)).Count.Should().Be(0);
        (await SuccessesAsync(act)).Should().Be(0);
        await using (var app = await source.OpenConnectionAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
            (await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
                "SELECT count(*) FROM public.tenant_locales WHERE tenant_id=@tenant",
                ("tenant", SeedData.English.TenantId.Value))).Should().Be(0);
        }
        var refused = await SendAsync(source, evidence, act.Command);
        refused.IsSuccess.Should().BeFalse("a caller omission cannot replace the independently captured fixed locale vector");
        refused.Error!.Code.Should().Be("concurrency_conflict");
        (await SnapshotAsync(act)).Count.Should().Be(0);
        (await SuccessesAsync(act)).Should().Be(0);
        (await runner.RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);
        (await SnapshotAsync(act)).Count.Should().Be(1);
        (await SuccessesAsync(act)).Should().Be(1);
        var installed = await AuditCountAsync(source);
        (await runner.RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);
        (await AuditCountAsync(source)).Should().Be(installed);
    }

    [Fact]
    public async Task Fixed_branding_refuses_before_Customization_completion_and_keeps_the_prefix_resumable()
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(_database);
        await PrepareAsync(source, evidence, Describe("builtin-content"));
        var theme = SeedData.English.Curriculum!.Theme;
        var request = new SetTenantBrandingCommand(theme.Id, theme.Value, null);
        var attempted = new ContentAct(request, "tenancy.setting.write", "tenant_settings", "",
            "SELECT count(*),coalesce(max(row_version),-1) FROM public.tenant_settings WHERE id=@target", theme.Id);
        var before = await SnapshotAsync(attempted);
        var successes = await SuccessesAsync(attempted);

        var refused = await SendAsync(source, evidence, request);

        refused.IsSuccess.Should().BeFalse("a theme cannot close the still-missing Customization predecessor");
        refused.Error!.Code.Should().Be("concurrency_conflict");
        (await SnapshotAsync(attempted)).Should().Be(before);
        (await SuccessesAsync(attempted)).Should().Be(successes);
        var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance,
            authorizationEvidence: evidence.Source), NullLogger<SeedRunner>.Instance);
        (await runner.RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);
        var installed = await AuditCountAsync(source);
        (await runner.RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);
        (await AuditCountAsync(source)).Should().Be(installed);
    }

    [Theory]
    [InlineData("register-content", false)]
    [InlineData("register-content", true)]
    [InlineData("create-course", false)]
    [InlineData("create-course", true)]
    public async Task Ordinary_stage_proof_refuses_unknown_Role_ids_and_premature_tombstoning_cannot_remove_history(string kind, bool attemptTombstone)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(_database);
        var act = Describe(kind);
        await PrepareAsync(source, evidence, act);
        var before = await SnapshotAsync(act);
        var role = Guid.CreateVersion7();
        var template = IdentityFoundationManifest.Version1.Templates.Single(value =>
            value.BuiltInKey == BuiltInRoleKey.Learner && value.Scope == PermissionScope.Organization);
        await using (var app = await source.OpenConnectionAsync())
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
            await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, SeedData.English.TenantId.Value, null, false);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                INSERT INTO public.roles(id,tenant_id,built_in_key,permission_scope,created_at,created_by)
                    VALUES(@role,@tenant,'Learner','Organization',clock_timestamp(),@actor)
                """, ("role", role), ("tenant", SeedData.English.TenantId.Value), ("actor", UserId.SystemActor.Value));
            foreach (var grant in template.Grants)
                await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                    INSERT INTO public.role_permission_grants(tenant_id,role_id,permission_scope,permission_key)
                        VALUES(@tenant,@role,@scope,@key)
                    """, ("tenant", SeedData.English.TenantId.Value), ("role", role),
                    ("scope", grant.Scope.ToString()), ("key", grant.Key));
            await transaction.CommitAsync();
        }
        if (attemptTombstone)
        {
            // Before Membership history, an application Role can only be inserted
            // as a complete live template. A tombstone-only early prefix cannot be
            // manufactured through the app role, and this test keeps that guard on.
            await using var app = await source.OpenConnectionAsync();
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
            await IdentityMembershipFixture.LockAsync(app, transaction, SeedData.English.TenantId.Value, null, false);
            await FluentActions.Awaiting(() => IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.roles SET deleted_at=clock_timestamp(),deleted_by=@actor,
                    updated_at=clock_timestamp(),updated_by=@actor,row_version=row_version+1 WHERE id=@role
                """, ("role", role), ("actor", UserId.SystemActor.Value)))
                .Should().ThrowAsync<PostgresException>().Where(exception => exception.SqlState == "P0304");
        }
        var refused = await SendAsync(source, evidence, act.Command);
        refused.IsSuccess.Should().BeFalse();
        refused.Error!.Code.Should().Be("concurrency_conflict");
        (await SnapshotAsync(act)).Should().Be(before);
        (await SuccessesAsync(act)).Should().Be(0);
    }

    [Theory]
    [InlineData("builtin-content")]
    [InlineData("builtin-taxonomy")]
    [InlineData("register-content")]
    [InlineData("publish-content")]
    [InlineData("register-taxonomy")]
    [InlineData("publish-taxonomy")]
    [InlineData("create-course")]
    [InlineData("translate-course")]
    [InlineData("publish-course")]
    [InlineData("create-lesson")]
    [InlineData("translate-lesson")]
    [InlineData("publish-lesson")]
    public async Task Every_pristine_interrupted_content_act_resumes_and_the_completed_rerun_writes_no_audit(string kind)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(_database);
        await PrepareAsync(source, evidence, Describe(kind));
        var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance,
            authorizationEvidence: evidence.Source), NullLogger<SeedRunner>.Instance);
        (await runner.RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);
        var before = await AuditCountAsync(source);
        (await runner.RunAsync(CancellationToken.None, [SeedData.English])).Should().Be(0);
        (await AuditCountAsync(source)).Should().Be(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_noncanonical_request_refuses_before_the_Tenant_fence_and_is_not_reported_as_a_race(bool education)
    {
        await using var source = ApplicationDataSource.Build(_database.AppConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(_database);
        var act = Describe(education ? "create-course" : "register-content");
        await PrepareAsync(source, evidence, act);
        var before = await SnapshotAsync(act);
        object changed = act.Command switch
        {
            CreateCourseCommand value => value with { SlugKey = "not-declared" },
            RegisterTenantContentTypeCommand value => value with { Key = "not-declared" },
            _ => throw new InvalidOperationException("The fixture request is not mutable."),
        };
        await using var app = await source.OpenConnectionAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
        await IdentityMembershipFixture.LockAsync(app, transaction, SeedData.English.TenantId.Value, null, false);
        var result = await SendAsync(source, evidence, changed).WaitAsync(TimeSpan.FromSeconds(1));
        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("business_rule_violation");
        (await SnapshotAsync(act)).Should().Be(before);
        (await SuccessesAsync(act)).Should().Be(0);
    }

    [Theory]
    [InlineData("builtin-content", false)]
    [InlineData("builtin-taxonomy", false)]
    [InlineData("register-content", false)]
    [InlineData("publish-content", false)]
    [InlineData("register-taxonomy", false)]
    [InlineData("publish-taxonomy", false)]
    [InlineData("create-course", false)]
    [InlineData("translate-course", false)]
    [InlineData("publish-course", false)]
    [InlineData("create-lesson", false)]
    [InlineData("translate-lesson", false)]
    [InlineData("publish-lesson", false)]
    [InlineData("builtin-content", true)]
    [InlineData("builtin-taxonomy", true)]
    [InlineData("register-content", true)]
    [InlineData("publish-content", true)]
    [InlineData("register-taxonomy", true)]
    [InlineData("publish-taxonomy", true)]
    [InlineData("create-course", true)]
    [InlineData("translate-course", true)]
    [InlineData("publish-course", true)]
    [InlineData("create-lesson", true)]
    [InlineData("translate-lesson", true)]
    [InlineData("publish-lesson", true)]
    public async Task All_twelve_fixed_content_writers_serialize_fresh_prerequisites_with_Tenant_edits(string kind, bool seedFirst)
    {
        const long pauseKey = -9223372036854760000L;
        var application = "seed-content-fence-" + Guid.NewGuid().ToString("N");
        await using var source = ApplicationDataSource.Build(new NpgsqlConnectionStringBuilder(_database.AppConnectionString)
        { ApplicationName = application }.ConnectionString);
        await using var evidence = await ComponentAuthorizationEvidence.CreateAsync(_database);
        var act = Describe(kind);
        await PrepareAsync(source, evidence, act);
        var before = await SnapshotAsync(act);
        var successes = await SuccessesAsync(act);
        await using var editor = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var edit = await editor.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(editor, edit);
        await IdentityMembershipFixture.ScopeAsync(editor, edit, SeedData.English.TenantId.Value);
        if (!seedFirst)
        {
            await IdentityMembershipFixture.LockAsync(editor, edit, SeedData.English.TenantId.Value, null, false);
            var writing = SendAsync(source, evidence, act.Command);
            await WaitForWriterAsync(application, editor.ProcessID, writing);
            await EditTenantAsync(editor, edit);
            await edit.CommitAsync();
            var refused = await writing;
            refused.IsSuccess.Should().BeFalse();
            refused.Error!.Code.Should().Be("concurrency_conflict", "a valid fixed request found an edited fresh prerequisite");
            (await SnapshotAsync(act)).Should().Be(before);
            (await SuccessesAsync(act)).Should().Be(successes);
        }
        else
        {
            await using var pause = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await using var paused = await pause.BeginTransactionAsync();
            await IdentityMembershipFixture.ExecuteAsync(pause, paused,
                "SELECT pg_catalog.pg_advisory_xact_lock(@key)", ("key", pauseKey));
            // Only fixture trigger installation uses the deployment role. The caller,
            // the editor, lock observations and durable assertions use learnstack_app.
            await DeploymentAsync($$"""
                CREATE FUNCTION public.proof_pause_content() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF {{act.Predicate}} THEN
                        IF COALESCE(current_setting('app.scope',true),'') <> '' THEN
                            RAISE EXCEPTION 'content writer retained elevated read scope' USING ERRCODE='23514';
                        END IF;
                        PERFORM pg_catalog.pg_advisory_xact_lock({{pauseKey}});
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER proof_pause_content BEFORE INSERT OR UPDATE ON public.{{act.Table}}
                    FOR EACH ROW EXECUTE FUNCTION public.proof_pause_content();
                """);
            var writing = SendAsync(source, evidence, act.Command);
            try
            {
                var producer = await WaitForWriterAsync(application, pause.ProcessID, writing);
                var admission = IdentityMembershipFixture.LockAsync(editor, edit, SeedData.English.TenantId.Value, null, false);
                await WaitForEdgeAsync(editor.ProcessID, producer, admission);
                admission.IsCompleted.Should().BeFalse("the exclusive seed fence lasts through its actual business commit");
                await paused.CommitAsync();
                (await writing).IsSuccess.Should().BeTrue();
                await admission.WaitAsync(TimeSpan.FromSeconds(5));
                var installed = await SnapshotAsync(act);
                installed.Count.Should().Be(1);
                installed.Version.Should().BeGreaterThan(before.Version);
                (await SuccessesAsync(act)).Should().Be(successes + 1);
                await EditTenantAsync(editor, edit);
                await edit.CommitAsync();
                (await SnapshotAsync(act)).Should().Be(installed);
            }
            finally
            {
                await paused.DisposeAsync();
                await edit.DisposeAsync();
                await writing.WaitAsync(TimeSpan.FromSeconds(15));
                await DeploymentAsync($"DROP TRIGGER proof_pause_content ON public.{act.Table}; DROP FUNCTION public.proof_pause_content();");
            }
        }
        await using var check = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var checking = await check.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(check, checking, SeedData.English.TenantId.Value);
        (await IdentityMembershipFixture.ScalarAsync<string>(check, checking,
            "SELECT display_name FROM public.tenants WHERE id=@tenant", ("tenant", SeedData.English.TenantId.Value)))
            .Should().Be("Preserved competing content institution");
    }

    private async Task PrepareAsync(NpgsqlDataSource source, ComponentAuthorizationEvidence evidence, ContentAct act)
    {
        // MUST append failure rolls back that act. The four Education SHOULD
        // operations instead fail their actual INSERT, since optional audit failure
        // must not roll back their business change. Earlier acts are genuine commits.
        var businessFault = act.Command is CreateCourseCommand or AddCourseTranslationCommand
            or CreateLessonCommand or AddLessonTranslationCommand;
        var table = businessFault ? act.Table : "audit_log";
        var predicate = businessFault ? act.Predicate
            : $"NEW.operation='{act.Operation}' AND NEW.tenant_id='{SeedData.English.TenantId.Value:D}'::uuid AND NEW.outcome='success'";
        await DeploymentAsync($$"""
            CREATE FUNCTION public.proof_interrupt_content() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF {{predicate}} THEN
                    RAISE EXCEPTION 'fixed content interruption' USING ERRCODE='23514';
                END IF;
                RETURN NEW;
            END $$;
            CREATE TRIGGER proof_interrupt_content BEFORE INSERT ON public.{{table}}
                FOR EACH ROW EXECUTE FUNCTION public.proof_interrupt_content();
            """);
        try
        {
            var runner = new SeedRunner(context => SeedComposition.Build(source, context, NullLoggerFactory.Instance,
                authorizationEvidence: evidence.Source), NullLogger<SeedRunner>.Instance);
            await FluentActions.Awaiting(() => runner.RunAsync(CancellationToken.None, [SeedData.English]))
                .Should().ThrowAsync<Exception>();
        }
        finally { await DeploymentAsync($"DROP TRIGGER proof_interrupt_content ON public.{table}; DROP FUNCTION public.proof_interrupt_content();"); }
        (await SuccessesAsync(act)).Should().Be(0);
    }

    private static ContentAct Describe(string kind)
    {
        var data = SeedData.English.Curriculum!;
        var course = data.Courses[0];
        var lesson = course.Lessons[0];
        var ct = course.Translations[0];
        var lt = lesson.Translations[0];
        object command = kind switch
        {
            "builtin-content" => new InitializeTenantContentTypeDefaultCommand(),
            "builtin-taxonomy" => new InitializeTenantLevelTaxonomyDefaultCommand(),
            "register-content" => new RegisterTenantContentTypeCommand(data.ContentType.Id, data.ContentType.Key,
                data.ContentType.SchemaVersion, data.ContentType.DisplayName, data.ContentType.JsonSchema, data.ContentType.RendererKey),
            "publish-content" => new PublishTenantContentTypeCommand(data.ContentType.Id, RequireNoIncumbent: true),
            "register-taxonomy" => new RegisterTenantLevelTaxonomyCommand(data.Taxonomy.Id, data.Taxonomy.Key,
                data.Taxonomy.SchemaVersion, data.Taxonomy.DisplayName,
                [.. data.Taxonomy.Bands.Select(band => new TaxonomyItemInput(band.Key, band.DisplayName, band.Sort, band.Metadata))]),
            "publish-taxonomy" => new PublishTenantLevelTaxonomyCommand(data.Taxonomy.Id, RequireNoIncumbent: true),
            "create-course" => new CreateCourseCommand(course.Id, course.SlugKey, course.ContentAccess,
                course.LevelTaxonomyKey, course.LevelTaxonomySchemaVersion, course.LevelBandKey),
            "translate-course" => new AddCourseTranslationCommand(course.Id, 0, ct.Locale, ct.Title, ct.Summary, ct.Slug),
            "publish-course" => new PublishCourseCommand(course.Id, course.Translations.Length),
            "create-lesson" => new CreateLessonCommand(lesson.Id, course.Id, lesson.Sort, lesson.ContentTypeKey, lesson.ContentTypeSchemaVersion),
            "translate-lesson" => new AddLessonTranslationCommand(lesson.Id, 0, lt.Locale, lt.Title, lt.Slug, lt.Body),
            "publish-lesson" => new PublishLessonCommand(lesson.Id, lesson.Translations.Length),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var operation = kind switch
        {
            "builtin-content" => "customization.content_type.initialize",
            "builtin-taxonomy" => "customization.level_taxonomy.initialize",
            "register-content" => "customization.content_type.register",
            "publish-content" => "customization.content_type.publish",
            "register-taxonomy" => "customization.level_taxonomy.register",
            "publish-taxonomy" => "customization.level_taxonomy.publish",
            "create-course" => "education.course.create",
            "translate-course" => "education.course.translation_add",
            "publish-course" => "education.course.publish",
            "create-lesson" => "education.lesson.create",
            "translate-lesson" => "education.lesson.translation_add",
            "publish-lesson" => "education.lesson.publish",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var (table, rootTable, column, target) = kind switch
        {
            "builtin-content" => ("tenant_content_types", "tenant_content_types", "key", (object)BuiltInCustomizations.Card.Key),
            "builtin-taxonomy" => ("tenant_level_taxonomies", "tenant_level_taxonomies", "key", (object)BuiltInCustomizations.Plain.Key),
            "register-content" or "publish-content" => ("tenant_content_types", "tenant_content_types", "id", (object)data.ContentType.Id),
            "register-taxonomy" or "publish-taxonomy" => ("tenant_level_taxonomies", "tenant_level_taxonomies", "id", (object)data.Taxonomy.Id),
            "create-course" or "publish-course" => ("courses", "courses", "id", (object)course.Id),
            "translate-course" => ("course_translations", "courses", "course_id", (object)course.Id),
            "create-lesson" or "publish-lesson" => ("lessons", "lessons", "id", (object)lesson.Id),
            "translate-lesson" => ("lesson_translations", "lessons", "lesson_id", (object)lesson.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var predicate = $"NEW.{column}='{target}'";
        if (column != "key") predicate += "::uuid";
        predicate += $" AND NEW.tenant_id='{SeedData.English.TenantId.Value:D}'::uuid";
        var snapshot = $"SELECT count(*),coalesce(max(row_version),-1) FROM public.{rootTable} WHERE {(column == "key" ? "key" : "id")}=@target";
        return new(command, operation, table, predicate, snapshot, target);
    }

    private static async Task<IResultBase> SendAsync(NpgsqlDataSource source, ComponentAuthorizationEvidence evidence, object request)
    {
        await using var provider = SeedComposition.Build(source, new SeedTenantContext(SeedData.English.TenantId, null),
            NullLoggerFactory.Instance, authorizationEvidence: evidence.Source);
        await using var scope = provider.CreateAsyncScope();
        return (IResultBase)(await scope.ServiceProvider.GetRequiredService<ISender>().Send(request))!;
    }

    private static Task EditTenantAsync(NpgsqlConnection app, NpgsqlTransaction transaction) =>
        IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            UPDATE public.tenants SET display_name='Preserved competing content institution',
                row_version=row_version+1,updated_at=clock_timestamp(),updated_by=@actor WHERE id=@tenant
            """, ("actor", SeedData.IdentityUsers[0].UserId.Value), ("tenant", SeedData.English.TenantId.Value));

    private async Task<ContentSnapshot> SnapshotAsync(ContentAct act)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
        await using var command = new NpgsqlCommand(act.SnapshotSql, app, transaction);
        command.Parameters.AddWithValue("target", act.Target);
        await using var rows = await command.ExecuteReaderAsync();
        (await rows.ReadAsync()).Should().BeTrue();
        return new(rows.GetInt64(0), rows.GetInt64(1));
    }

    private async Task<long> SuccessesAsync(ContentAct act)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
        return await IdentityMembershipFixture.ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM public.audit_log WHERE operation=@operation AND outcome='success'", ("operation", act.Operation));
    }

    private static async Task<long> AuditCountAsync(NpgsqlDataSource source)
    {
        await using var app = await source.OpenConnectionAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, SeedData.English.TenantId.Value);
        return await IdentityMembershipFixture.ScalarAsync<long>(app, transaction, "SELECT count(*) FROM public.audit_log");
    }

    private async Task<int> WaitForWriterAsync(string application, int blocker, Task writing)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(1.5) && !writing.IsCompleted)
        {
            var pid = await IdentityMembershipFixture.ScalarAsync<int>(app, null, """
                SELECT coalesce((SELECT pid FROM pg_catalog.pg_stat_activity WHERE application_name=@application
                    AND @blocker=ANY(pg_catalog.pg_blocking_pids(pid)) LIMIT 1),0)
                """, ("application", application), ("blocker", blocker));
            if (pid != 0) return pid;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The real fixed content writer did not reach the competing fence.");
    }

    private async Task WaitForEdgeAsync(int waiter, int blocker, Task waiting)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < TimeSpan.FromSeconds(1.5) && !waiting.IsCompleted)
        {
            if (await IdentityMembershipFixture.ScalarAsync<bool>(app, null,
                "SELECT @blocker=ANY(pg_catalog.pg_blocking_pids(@waiter))", ("blocker", blocker), ("waiter", waiter))) return;
            await Task.Delay(10);
        }
        throw new InvalidOperationException("The ordinary editor did not wait for the fixed content business owner.");
    }

    private async Task DeploymentAsync(string sql)
    {
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(connection, null, sql);
    }

    private sealed record ContentAct(object Command, string Operation, string Table, string Predicate, string SnapshotSql, object Target);
    private sealed record ContentSnapshot(long Count, long Version);
}
