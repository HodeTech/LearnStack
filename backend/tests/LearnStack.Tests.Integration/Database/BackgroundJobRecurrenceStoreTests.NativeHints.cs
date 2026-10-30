using System.Data.Common;
using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.Storage;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Fact]
    public async Task Real_native_manager_cannot_replace_the_newest_tick_with_an_obsolete_revision()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var today = await TodayAsync(database);
        var first = Fixture.Manifest(today.AddDays(-2), today.AddDays(-2));
        var next = Fixture.Manifest(today.AddDays(-1), first.Facts.Anchor, revision: 2);
        await AssertRegistrationAsync(Store(source), first, BackgroundJobRecurrenceRegistrationResult.Registered);
        var storage = NativeStorage(source);
        RegisterNative(storage, first);
        await using var read = await source.OpenConnectionAsync();
        var firstPayload = await NativePayloadAsync(read, first.Facts.Key);
        await AssertRegistrationAsync(Store(source), next, BackgroundJobRecurrenceRegistrationResult.Registered);
        // Retained obsolete hints are valid finite data while repair is pending.
        _ = NativeStorage(source);
        RegisterNative(storage, next);
        var currentPayload = await NativePayloadAsync(read, first.Facts.Key);
        currentPayload.Should().NotBe(firstPayload);
        var obsolete = () => RegisterNative(storage, first);
        RequireNativeMetadataRefusal(obsolete.Should().Throw<Exception>().Which);
        (await NativePayloadAsync(read, first.Facts.Key)).Should().Be(currentPayload);
        RegisterNative(storage, next); // Unchanged stock changedFields may omit Job.
        (await NativePayloadAsync(read, first.Facts.Key)).Should().Be(currentPayload);
        (await ReadAsync<long>(read, "SELECT count(*) FROM background_job_occurrences")).Should().Be(0);
        (await ReadAsync<long>(read, "SELECT count(*) FROM background_job_recipients")).Should().Be(0);
    }

    [Theory]
    [InlineData("STABLE")]
    [InlineData("IMMUTABLE")]
    public async Task Bootstrap_rejects_snapshot_volatility_drift_with_the_same_function_body(string volatility)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var before = await ReadAsync<string>(owner, "SELECT prosrc FROM pg_proc WHERE oid='public.fn_hangfire_metadata_guard()'::regprocedure");
        await SchemaQueries.ExecuteAsync(owner, null, "ALTER FUNCTION public.fn_hangfire_metadata_guard() " + volatility);
        try
        {
            (await ReadAsync<string>(owner, "SELECT prosrc FROM pg_proc WHERE oid='public.fn_hangfire_metadata_guard()'::regprocedure"))
                .Should().Be(before);
            var refuse = () => NativeStorage(source);
            refuse.Should().Throw<HangfireStorageRefusedException>().Which.InnerException.Should().BeNull();
        }
        finally { await SchemaQueries.ExecuteAsync(owner, null, "ALTER FUNCTION public.fn_hangfire_metadata_guard() VOLATILE"); }
        _ = NativeStorage(source);
    }

    [Fact]
    public async Task Native_revision_guard_down_up_and_independent_canonical_replacement_preserve_the_storage_shape()
    {
        await using var database = await DisposableSchemaDatabase.CreateAtPlatformMigrationAsync(schema.Postgres,
            "20261017225713_fence_hangfire_recurrence_revision");
        await using var source = Source(database);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await using var context = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql(database.MigrationConnectionString,
                options => options.MigrationsHistoryTable(PlatformDbContextFactory.HistoryTable)).Options);
        const string correction = "20261017225713_fence_hangfire_recurrence_revision";
        // Qualify this one-function migration at its own boundary; later
        // additive control indexes are independently restored afterward.
        await context.GetService<IMigrator>().MigrateAsync(correction);
        var shape = await NativeStorageShapeAsync(owner);
        var current = await NativeGuardDefinitionAsync(owner);
        await context.GetService<IMigrator>().MigrateAsync("20261017184321_add_background_job_retirement_index");
        (await NativeGuardDefinitionAsync(owner)).Should().NotBe(current);
        (await NativeStorageShapeAsync(owner)).Should().Equal(shape);
        var refuse = () => NativeStorage(source);
        refuse.Should().Throw<HangfireStorageRefusedException>();

        // Prove the additive migration upgrades an actual old body on its own.
        // The fresh chain already reads the current embedded Guards resource.
        await context.GetService<IMigrator>().MigrateAsync(correction);
        (await NativeGuardDefinitionAsync(owner)).Should().Be(current);
        (await NativeStorageShapeAsync(owner)).Should().Equal(shape);
        _ = NativeStorage(source);
        await context.GetService<IMigrator>().MigrateAsync("20261017184321_add_background_job_retirement_index");
        (await NativeGuardDefinitionAsync(owner)).Should().NotBe(current);
        refuse.Should().Throw<HangfireStorageRefusedException>();

        // Apply the independent canonical function to the proven older body,
        // rather than replaying a no-op over an already correct migration.
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(RepositoryPath.RolesScript())!, "../../.."));
        var document = await File.ReadAllTextAsync(Path.Combine(root, "docs/standards/05-database.md"));
        const string marker = "CREATE FUNCTION public.fn_hangfire_metadata_guard() RETURNS trigger\n";
        var start = document.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThan(0);
        var end = document.IndexOf("END $$;", start, StringComparison.Ordinal) + "END $$;".Length;
        await SchemaQueries.ExecuteAsync(owner, null, document[start..end]
            .Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal));
        (await NativeGuardDefinitionAsync(owner)).Should().Be(current);
        _ = NativeStorage(source);
        await context.GetService<IMigrator>().MigrateAsync(correction);
        (await NativeGuardDefinitionAsync(owner)).Should().Be(current);
        (await NativeStorageShapeAsync(owner)).Should().Equal(shape);
        await context.Database.MigrateAsync();
        (await context.Database.GetAppliedMigrationsAsync()).Should().Equal(context.Database.GetMigrations());
        context.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private static Hangfire.PostgreSql.PostgreSqlStorage NativeStorage(PrivilegedDataSource source) =>
        HangfireStorageBootstrap.Create(source, Fixture.Definitions().Infrastructure, new HangfireBoundedLogProvider());

    private static void RegisterNative(JobStorage storage, BackgroundJobRecurrenceDescriptor manifest)
    {
        var job = new Job(typeof(HangfireRecurrenceTick), HangfireInvocationContract.TickMethod,
            [manifest.Facts.Key, manifest.Facts.Revision], "control");
        new RecurringJobManager(storage, HangfireRuntimeFilters.Create()).AddOrUpdate(manifest.Facts.Key,
            job, "* * * * *", new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc, MisfireHandling = MisfireHandlingMode.Relaxed });
    }

    private static Task<string> NativePayloadAsync(DbConnection connection, string key) =>
        ReadAsync<string>(connection, "SELECT value FROM public.hash WHERE key='recurring-job:'||@key AND field='Job'", ("key", key));

    private static Task<string> NativeGuardDefinitionAsync(DbConnection connection) =>
        ReadAsync<string>(connection, "SELECT pg_get_functiondef('public.fn_hangfire_metadata_guard()'::regprocedure)");

    private static Task<List<string>> NativeStorageShapeAsync(DbConnection connection) => SchemaQueries.ReadStringsAsync(connection, """
        SELECT item FROM (
          SELECT 'relation:'||c.oid||':'||c.relname||':'||c.relkind::text||':'||c.relowner||':'||coalesce(c.relacl::text,'') AS item
            FROM pg_class c WHERE c.relnamespace='public'::regnamespace
          UNION ALL
          SELECT 'column:'||attrelid||':'||attnum||':'||attname||':'||atttypid||':'||coalesce(attacl::text,'')
            FROM pg_attribute WHERE attrelid IN (SELECT oid FROM pg_class WHERE relnamespace='public'::regnamespace)
              AND attnum>0 AND NOT attisdropped
          UNION ALL
          SELECT 'trigger:'||tgrelid||':'||tgname||':'||tgenabled::text||':'||pg_get_triggerdef(oid)
            FROM pg_trigger WHERE tgrelid IN (SELECT oid FROM pg_class WHERE relnamespace='public'::regnamespace)
              AND NOT tgisinternal
          UNION ALL
          SELECT 'function:'||oid||':'||proowner||':'||coalesce(proacl::text,'')
            FROM pg_proc WHERE oid='public.fn_hangfire_metadata_guard()'::regprocedure
        ) shape ORDER BY item
        """);

    private static void RequireNativeMetadataRefusal(Exception failure)
    {
        while (failure is not PostgresException && failure.InnerException is not null) failure = failure.InnerException;
        failure.Should().BeOfType<PostgresException>().Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        ((PostgresException)failure).MessageText.Should().Be("hangfire_metadata_refused");
    }
}
