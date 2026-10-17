using System.Data.Common;
using FluentAssertions;
using Hangfire.Storage;
using LearnStack.Infrastructure.Jobs;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Actual_vendor_transaction_blocks_nonkey_supersession_and_key_share_control_does_not(bool weakened)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var today = await TodayAsync(database);
        var manifest = Fixture.Manifest(today.AddDays(-2), today.AddDays(-2));
        await AssertRegistrationAsync(Store(source), manifest, BackgroundJobRecurrenceRegistrationResult.Registered);
        var storage = NativeStorage(source);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        if (weakened)
            await ReplaceNativeGuardAsync(owner, definition => definition.Replace("FOR SHARE;", "FOR KEY SHARE;", StringComparison.Ordinal));
        await using var blocker = await source.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(blocker, transaction,
            "INSERT INTO public.set(key,value,score) VALUES ('recurring-jobs',@key,0)", ("key", manifest.Facts.Key));
        // Stock writes Job before the set. This actual set-row wait witnesses
        // that the preceding Job trigger has run in the same vendor transaction.
        var native = Task.Run(() => RegisterNative(storage, manifest));
        Task<int>? supersede = null;
        var released = false;
        try
        {
            var vendor = await WaitForNativeBlockAsync(source, native, blocker.ProcessID);
            vendor.Should().NotBeNull();
            await using var competing = await source.OpenConnectionAsync();
            await using var update = new NpgsqlCommand("""
                UPDATE background_job_recurrences SET superseded_at=@at
                WHERE recurrence_key=@key AND revision=1
                """, competing) { CommandTimeout = 5 };
            update.Parameters.AddWithValue("at", today.AddDays(-1));
            update.Parameters.AddWithValue("key", manifest.Facts.Key);
            supersede = update.ExecuteNonQueryAsync();
            var waiting = await WaitForNativeBlockAsync(source, supersede, vendor!.Value);
            Action mustSerialize = () => waiting.Should().NotBeNull(
                "a non-key supersession cannot commit before the actual Job write transaction ends");
            if (weakened) mustSerialize.Should().Throw<Xunit.Sdk.XunitException>();
            else mustSerialize();
            await transaction.CommitAsync();
            released = true;
            await native;
            (await supersede).Should().Be(1);
        }
        finally
        {
            if (!released) await transaction.RollbackAsync();
            await ObserveNativeTaskAsync(native);
            if (supersede is not null) await ObserveNativeTaskAsync(supersede);
        }
    }

    [Theory]
    [InlineData("manifest", false)]
    [InlineData("manifest", true)]
    [InlineData("hash", false)]
    [InlineData("hash", true)]
    public async Task Native_replacement_rechecks_activation_after_real_manifest_or_pretrigger_hash_waits(string wait, bool weakened)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var today = await TodayAsync(database);
        var first = Fixture.Manifest(today.AddDays(-3), today.AddDays(-3));
        var second = Fixture.Manifest(today.AddDays(-2), first.Facts.Anchor, revision: 2);
        var third = Fixture.Manifest(today.AddDays(-1), first.Facts.Anchor, revision: 3);
        await AssertRegistrationAsync(Store(source), first, BackgroundJobRecurrenceRegistrationResult.Registered);
        var storage = NativeStorage(source);
        RegisterNative(storage, first);
        await using var read = await source.OpenConnectionAsync();
        var original = await NativePayloadAsync(read, first.Facts.Key);
        await AssertRegistrationAsync(Store(source), second, BackgroundJobRecurrenceRegistrationResult.Registered);
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        if (weakened)
        {
            await ReplaceNativeGuardAsync(owner, definition => wait == "manifest"
                ? definition.Replace("AND superseded_at IS NULL", "", StringComparison.Ordinal)
                : definition.Replace("FOR SHARE;", ";", StringComparison.Ordinal));
            if (wait == "hash")
                await SchemaQueries.ExecuteAsync(owner, null, "ALTER FUNCTION public.fn_hangfire_metadata_guard() STABLE");
        }
        await using var blocker = await source.OpenConnectionAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        if (wait == "manifest") await InsertNativeManifestAsync(blocker, transaction, third);
        else await SchemaQueries.ExecuteAsync(blocker, transaction,
            "SELECT id FROM public.hash WHERE key='recurring-job:'||@key AND field='Job' FOR UPDATE", ("key", first.Facts.Key));
        var native = Task.Run(() => RegisterNative(storage, second));
        var released = false;
        try
        {
            (await WaitForNativeBlockAsync(source, native, blocker.ProcessID)).Should().NotBeNull(
                "the actual stock write must reach the selected database barrier before activation changes");
            if (wait == "hash")
                await AssertRegistrationAsync(Store(source), third, BackgroundJobRecurrenceRegistrationResult.Registered);
            await transaction.CommitAsync();
            released = true;
            var failure = await Record.ExceptionAsync(() => native);
            Action mustRefuse = () => failure.Should().NotBeNull(
                "the same actual old Job replacement must be refused after a newer durable activation");
            if (weakened) mustRefuse.Should().Throw<Xunit.Sdk.XunitException>();
            else { mustRefuse(); RequireNativeMetadataRefusal(failure!); }
            var retained = await NativePayloadAsync(read, first.Facts.Key);
            if (weakened) retained.Should().NotBe(original);
            else retained.Should().Be(original, "the entire stock vendor transaction rolls back");
            (await ReadAsync<long>(read, "SELECT revision FROM background_job_recurrences WHERE superseded_at IS NULL"))
                .Should().Be(3);
        }
        finally
        {
            if (!released) await transaction.RollbackAsync();
            await ObserveNativeTaskAsync(native);
        }
    }

    private static async Task ReplaceNativeGuardAsync(DbConnection owner, Func<string, string> mutate)
    {
        var original = await NativeGuardDefinitionAsync(owner);
        var changed = mutate(original);
        changed.Should().NotBe(original);
        await SchemaQueries.ExecuteAsync(owner, null, changed);
    }

    private static Task InsertNativeManifestAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        BackgroundJobRecurrenceDescriptor manifest) => SchemaQueries.ExecuteAsync(connection, transaction, """
            INSERT INTO background_job_recurrences (recurrence_key,revision,manifest_hash,target_definition_key,
                schedule,activates_at,anchor_at,period_microseconds,jitter_microseconds,recipient_policy,
                misfire_policy,next_scheduled_at)
            SELECT recurrence_key,@revision,@hash,target_definition_key,schedule,@activation,anchor_at,
                period_microseconds,jitter_microseconds,recipient_policy,misfire_policy,@activation
            FROM background_job_recurrences WHERE recurrence_key=@key AND superseded_at IS NULL
            """, ("revision", manifest.Facts.Revision), ("hash", manifest.ManifestHash),
            ("activation", manifest.Facts.ActivatesAt), ("key", manifest.Facts.Key));

    private static async Task<int?> WaitForNativeBlockAsync(LearnStack.Infrastructure.Persistence.PrivilegedDataSource source,
        Task operation, int blocker)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await using var observer = await source.OpenConnectionAsync(deadline.Token);
        while (true)
        {
            await using var query = new NpgsqlCommand("""
                SELECT pid FROM pg_stat_activity WHERE datname=current_database() AND usename=current_user
                    AND wait_event_type='Lock' AND @blocker=ANY(pg_blocking_pids(pid))
                """, observer) { CommandTimeout = 2 };
            query.Parameters.AddWithValue("blocker", blocker);
            if (await query.ExecuteScalarAsync(deadline.Token) is int pid) return pid;
            if (operation.IsCompleted) return null;
            await Task.Delay(5, deadline.Token);
        }
    }

    private static async Task ObserveNativeTaskAsync(Task operation)
    {
        try { await operation; }
        catch (Exception failure) when (failure is not OutOfMemoryException and not StackOverflowException) { }
    }
}
