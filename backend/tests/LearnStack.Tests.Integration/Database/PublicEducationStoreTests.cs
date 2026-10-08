using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.Tools.Seeder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace LearnStack.Tests.Integration.Database;

[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PublicEducationStoreTests(PublicReadFixture fixture, ITestOutputHelper output)
{
    private static TenantContext Host(SeedTenant tenant) => TenantContextFactory.Create(new TenantResolutionAttempt
    {
        HostTenantId = tenant.TenantId,
        HostOrganizationId = tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId : null,
    }).Value!;

    [Fact]
    public async Task Claim_narrowing_cannot_make_a_tenant_host_publish_organization_rows()
    {
        var tenant = SeedData.English;
        var context = TenantContextFactory.Create(new TenantResolutionAttempt
        {
            HasValidatedPrincipal = true,
            HostTenantId = tenant.TenantId,
            ClaimTenantId = tenant.TenantId,
            ClaimOrganizationId = tenant.DefaultOrganization.OrganizationId,
            UserId = UserId.From(Guid.NewGuid()),
            MembershipCovers = true,
            ClaimedOrganizationBelongsToTenant = true,
        }).Value!;
        await using var read = await Session.Open(fixture, context);
        (await read.Db.Courses.AsNoTracking().AnyAsync(course => course.SlugKey == "branch-conversation"))
            .Should().BeTrue("normal claim scope permits the branch; public host scope must independently remove it");
        (await read.Store.ReadCatalogAsync("en", null, 101, CancellationToken.None)).Select(row => row.Slug)
            .Should().Equal("foundation", "guided-practice");
        (await read.Store.ReadCourseAsync("branch-conversation", "en", CancellationToken.None)).Should().BeNull();
        (await read.Store.ReadLessonAsync("branch-conversation", "introductions", "en", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Tenant_scope_hatch_cannot_widen_the_public_hosts_explicit_predicates()
    {
        await using var read = await Session.Open(fixture, Host(SeedData.English));
        await using var command = new NpgsqlCommand("SELECT set_config('app.scope','tenant',true)",
            (NpgsqlConnection)read.Unit.Connection, (NpgsqlTransaction)read.Unit.Transaction!);
        await command.ExecuteScalarAsync();
        await using var visible = new NpgsqlCommand("SELECT count(*) FROM courses WHERE tenant_id=@tenant AND slug_key='branch-conversation'",
            (NpgsqlConnection)read.Unit.Connection, (NpgsqlTransaction)read.Unit.Transaction!);
        visible.Parameters.AddWithValue("tenant", SeedData.English.TenantId.Value);
        ((long)(await visible.ExecuteScalarAsync())!).Should().Be(1, "the hatch widens RLS but may not widen the public query");
        (await read.Db.Courses.AsNoTracking().AnyAsync(course => course.SlugKey == "branch-conversation")).Should().BeFalse("the normal filter remains intersected");
        (await read.Store.ReadCatalogAsync("en", null, 101, CancellationToken.None)).Select(row => row.Slug)
            .Should().Equal("foundation", "guided-practice");
        (await read.Store.ReadLessonAsync("branch-conversation", "introductions", "en", CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("claim-only")]
    [InlineData("ambient")]
    [InlineData("writable")]
    [InlineData("unannounced")]
    [InlineData("unenlisted")]
    public async Task Public_port_refuses_missing_provenance_or_read_transaction_before_data_sql(string mode)
    {
        var tenant = SeedData.English;
        ITenantContext context = mode switch
        {
            "claim-only" => TenantContextFactory.Create(new TenantResolutionAttempt
            {
                HasValidatedPrincipal = true,
                ClaimTenantId = tenant.TenantId,
                UserId = UserId.From(Guid.NewGuid()),
                MembershipCovers = true,
            }).Value!,
            "ambient" => new SeedTenantContext(tenant.TenantId, null),
            _ => Host(tenant),
        };
        await using var read = await Session.Open(fixture, context, mode == "writable" ? TransactionMode.ReadWrite : TransactionMode.ReadOnly,
            announce: mode != "unannounced", enlist: mode != "unenlisted");
        await ((Func<Task>)(async () => await read.Store.ReadCatalogAsync("en", null, 21, CancellationToken.None)))
            .Should().ThrowAsync<TenantContextMissingException>();
        read.Observer.Samples.Should().BeEmpty();
    }

    [Fact]
    public async Task Equal_timestamp_and_sort_anchors_use_postgresql_uuid_tiebreaking()
    {
        var english = SeedData.English;
        PublicCourseReadRow[] original;
        await using (var read = await Session.Open(fixture, Host(english)))
            original = (await read.Store.ReadCatalogAsync("en", null, 101, CancellationToken.None)).ToArray();
        var stamp = new DateTimeOffset(2026, 10, 3, 12, 34, 56, TimeSpan.Zero).AddTicks(1234560);
        foreach (var row in original)
            await fixture.ExecuteAsync(english, "UPDATE courses SET created_at=@stamp WHERE tenant_id=@tenant AND id=@id",
                new Dictionary<string, object> { ["stamp"] = stamp, ["id"] = row.Id.Value });
        try
        {
            await using var read = await Session.Open(fixture, Host(english));
            var first = (await read.Store.ReadCatalogAsync("en", null, 1, CancellationToken.None)).Single();
            first.Slug.Should().Be("foundation");
            first.CreatedAt.Should().Be(stamp);
            (await read.Store.ReadCatalogAsync("en", new CatalogContinuation(first.CreatedAt, first.Id), 101, CancellationToken.None))
                .Should().ContainSingle().Which.Slug.Should().Be("guided-practice");
        }
        finally
        {
            foreach (var row in original)
                await fixture.ExecuteAsync(english, "UPDATE courses SET created_at=@stamp WHERE tenant_id=@tenant AND id=@id",
                    new Dictionary<string, object> { ["stamp"] = row.CreatedAt, ["id"] = row.Id.Value });
        }
        var yoga = SeedData.Yoga;
        var parent = CourseId.From(yoga.Curriculum!.Courses[1].Id);
        await fixture.ExecuteAsync(yoga, "UPDATE lessons SET sort=0 WHERE tenant_id=@tenant AND course_id=@parent", new Dictionary<string, object> { ["parent"] = parent.Value }, yoga.DefaultOrganization.OrganizationId);
        try
        {
            await using var read = await Session.Open(fixture, Host(yoga));
            var first = (await read.Store.ReadOutlineAsync(parent, "en", null, 1, CancellationToken.None)).Single();
            first.Slug.Should().Be("tree-pose");
            (await read.Store.ReadOutlineAsync(parent, "en", new OutlineContinuation(parent, first.Sort, first.Id), 101, CancellationToken.None))
                .Should().ContainSingle().Which.Slug.Should().Be("child-pose");
        }
        finally
        {
            foreach (var lesson in yoga.Curriculum.Courses[1].Lessons)
                await fixture.ExecuteAsync(yoga, "UPDATE lessons SET sort=@sort WHERE tenant_id=@tenant AND id=@id",
                    new Dictionary<string, object> { ["sort"] = lesson.Sort, ["id"] = lesson.Id }, yoga.DefaultOrganization.OrganizationId);
        }
    }

    [Fact]
    public async Task Source_queries_project_no_body_before_eligibility_and_real_app_role_plans_are_recorded()
    {
        var tenant = SeedData.Yoga;
        await using var read = await Session.Open(fixture, Host(tenant));
        await using (var role = new NpgsqlCommand("SELECT current_user,current_setting('transaction_read_only'),rolsuper,rolbypassrls FROM pg_roles WHERE rolname=current_user",
            (NpgsqlConnection)read.Unit.Connection, (NpgsqlTransaction)read.Unit.Transaction!))
        await using (var rows = await role.ExecuteReaderAsync())
        {
            (await rows.ReadAsync()).Should().BeTrue();
            rows.GetString(0).Should().Be("learnstack_app"); rows.GetString(1).Should().Be("on");
            rows.GetBoolean(2).Should().BeFalse(); rows.GetBoolean(3).Should().BeFalse();
        }
        var catalog = await read.Store.ReadCatalogAsync("en", null, 21, CancellationToken.None);
        catalog.Should().HaveCount(3);
        var parent = (await read.Store.ReadCourseAsync("foundation", "en", CancellationToken.None))!;
        (await read.Store.ReadOutlineAsync(parent.Id, "en", null, 21, CancellationToken.None)).Should().HaveCount(2);
        (await read.Store.ReadLessonAsync("foundation", "tree-pose", "en", CancellationToken.None)).Should().NotBeNull();
        (await read.Store.ReadLessonAsync("guided-flow", "bridge-pose", "en", CancellationToken.None)).Should().BeNull();
        await read.Store.ReadCatalogAsync("en", new CatalogContinuation(catalog[0].CreatedAt, catalog[0].Id), 21, CancellationToken.None);
        await read.Store.ReadOutlineAsync(parent.Id, "en", new OutlineContinuation(parent.Id, 0, LessonId.From(tenant.Curriculum!.Courses[1].Lessons[0].Id)), 21, CancellationToken.None);
        var samples = read.Observer.Samples.ToArray();
        foreach (var sample in samples.Where(sample => !sample.Sql.Contains("P02d-4 lesson body", StringComparison.Ordinal)))
            sample.Sql.Should().NotContain(".body", "catalog, marketing and outline must not fetch lesson content");
        var body = samples.First(sample => sample.Sql.Contains("P02d-4 lesson body", StringComparison.Ordinal));
        body.Sql.Should().Contain("content_access = 'public'").And.Contain("status = 'published'").And.Contain("deleted_at IS NULL")
            .And.Contain("tenant_id").And.Contain("organization_id").And.Contain("locale").And.Contain(".body");
        var plans = new List<object>();
        foreach (var sample in samples.DistinctBy(sample => sample.Sql))
        {
            await using var command = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sample.Sql,
                (NpgsqlConnection)read.Unit.Connection, (NpgsqlTransaction)read.Unit.Transaction!);
            foreach (var parameter in sample.Parameters) command.Parameters.Add(parameter.Clone());
            var plan = (string)(await command.ExecuteScalarAsync())!;
            using var parsed = JsonDocument.Parse(plan);
            parsed.RootElement[0].TryGetProperty("Plan", out _).Should().BeTrue();
            plans.Add(new { Query = sample.Sql.Split('\n')[0], Plan = parsed.RootElement.Clone() });
        }
        output.WriteLine(JsonSerializer.Serialize(new { Sample = "real production SELECTs, local seeded PostgreSQL, app role, read-only; tiny cardinalities, no forced plan and no production p95 claim", Plans = plans }));
        read.Db.ChangeTracker.Entries().Should().BeEmpty();
    }

    private sealed record Sample(string Sql, NpgsqlParameter[] Parameters);
    private sealed class Observer : DbCommandInterceptor
    {
        public List<Sample> Samples { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Samples.Add(new Sample(command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(parameter => parameter.Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Session(AsyncServiceScope scope, IUnitOfWork unit, IUnitOfWorkScope frame, EducationDbContext db, ITenantContext context, Observer observer) : IAsyncDisposable
    {
        public IUnitOfWork Unit => unit;
        public EducationDbContext Db => db;
        public Observer Observer => observer;
        public PublicEducationReadStore Store { get; } = new(db, context, unit);
        public static async Task<Session> Open(PublicReadFixture fixture, ITenantContext context, TransactionMode mode = TransactionMode.ReadOnly, bool announce = true, bool enlist = true)
        {
            var scope = fixture.Services.CreateAsyncScope();
            // A fixed test scope, like SeedComposition: setting an AsyncLocal in
            // this async factory cannot flow backwards to its awaiting caller.
            var accessor = new StaticTenantContextAccessor(context);
            var unit = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var frame = await unit.BeginTransactionAsync(mode);
            if (announce) await unit.SetTenantContextAsync(context);
            var observer = new Observer();
            var db = new EducationDbContext(new DbContextOptionsBuilder<EducationDbContext>().UseNpgsql(unit.Connection)
                .AddInterceptors(new TenantContextGuardInterceptor(unit), observer).Options, accessor);
            if (enlist) await db.Database.UseTransactionAsync(unit.Transaction);
            return new Session(scope, unit, frame, db, context, observer);
        }
        public async ValueTask DisposeAsync()
        {
            await frame.DisposeAsync();
            await db.DisposeAsync();
            await scope.DisposeAsync();
        }
    }
}
