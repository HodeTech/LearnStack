using System.Diagnostics;
using FluentAssertions;
using Npgsql;
using Xunit;
using Xunit.Sdk;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0003 Amendment 6: parent-id updates and the lookup/FK race. Every data
/// operation authenticates as learnstack_app; committed owner setup is confined
/// to disposable databases and adds only test privileges and the observable gate.
/// </summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class EducationParentScopeTests(SchemaFixture schema)
{
    public static TheoryData<string> Children => new(
        "lessons", "course_translations", "lesson_translations");

    [Theory]
    [MemberData(nameof(Children))]
    public async Task Reparenting_Requires_An_Existing_Parent_With_The_Same_Nullable_Scope(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await EducationSchemaSeed.SeedAsync(database.AppConnectionString);
        await SetupAsync(database, $"GRANT UPDATE ON public.{table} TO learnstack_app");
        await using var app = await OpenAppAsync(database.AppConnectionString);
        var own = EducationSchemaSeed.Find(SchemaFixture.TenantA, SchemaFixture.OrgA1);
        var originalKey = table == "course_translations" ? own.CourseId : own.LessonId;
        var targets = new[]
        {
            ParentId(table, EducationSchemaSeed.Find(SchemaFixture.TenantA, null)),
            ParentId(table, EducationSchemaSeed.Find(SchemaFixture.TenantA, SchemaFixture.OrgA2)),
            ParentId(table, EducationSchemaSeed.Find(SchemaFixture.TenantB, null)),
            Guid.NewGuid(),
        };
        foreach (var target in targets)
        {
            await using var transaction = await app.BeginTransactionAsync();
            await AnnounceAsync(app, transaction);
            var update = async () => await ReparentAsync(app, transaction, table, originalKey, target);
            var refusal = (await update.Should().ThrowAsync<PostgresException>()).Which;
            refusal.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            refusal.MessageText.Should().Be($"parent scope is unavailable or mismatched (table {table})",
                "a visible tenant-wide, hidden sibling, foreign or missing target has one refusal");
        }

        var replacement = await CreateEmptyParentAsync(database.AppConnectionString, table);
        await using var accepted = await app.BeginTransactionAsync();
        await AnnounceAsync(app, accepted);
        (await ReparentAsync(app, accepted, table, originalKey, replacement)).Should().Be(1,
            "UPDATE must reach a row and permit a fresh parent in the same scope");
        await using var verify = new NpgsqlCommand(
            $"SELECT count(*) FROM public.{table} WHERE {ParentColumn(table)} = @parent "
            + (table == "lessons" ? "AND id = @id" : "AND locale = 'en'"), app, accepted);
        verify.Parameters.AddWithValue("parent", replacement);
        verify.Parameters.AddWithValue("id", originalKey);
        (await verify.ExecuteScalarAsync()).Should().Be(1L);
    }

    [Theory]
    [MemberData(nameof(Children))]
    public async Task A_Conflicting_Temporary_Parent_Cannot_Hide_The_Valid_Public_Parent(string table)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await EducationSchemaSeed.SeedAsync(database.AppConnectionString);
        var databaseName = new NpgsqlConnectionStringBuilder(database.MigrationConnectionString).Database;
        await SetupAsync(database, $"GRANT TEMPORARY ON DATABASE {databaseName} TO learnstack_app");
        await using var app = await OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction);
        var parent = ParentId(table, EducationSchemaSeed.Find(SchemaFixture.TenantA, SchemaFixture.OrgA1));
        await SchemaQueries.ExecuteAsync(app, transaction,
            $"CREATE TEMP TABLE {ParentTable(table)} (id uuid, tenant_id uuid, organization_id uuid) ON COMMIT DROP; "
            + $"INSERT INTO pg_temp.{ParentTable(table)} VALUES (@id, @tenant, NULL)",
            ("id", parent), ("tenant", SchemaFixture.TenantA));
        var childId = Guid.NewGuid();
        await EducationSchemaSeed.InsertAsync(app, transaction, table,
            SchemaFixture.TenantA, SchemaFixture.OrgA1, childId, parent, locale: "tr");
        (await SchemaQueries.CountAsync(app,
            $"SELECT count(*) FROM public.{table} WHERE {ParentColumn(table)} = '{parent}'"
            + (table == "lessons" ? $" AND id = '{childId}'" : " AND locale = 'tr'"), transaction)).Should().Be(1L,
                "a counterfeit temporary parent with the wrong organization must not veto the real parent");
    }

    [Theory]
    [MemberData(nameof(Children))]
    public Task The_Parent_Lookup_Locks_Before_The_Foreign_Key_Check(string table) =>
        ProveParentLookupLockAsync(table, removeLookupLock: false);

    [Theory]
    [MemberData(nameof(Children))]
    public async Task The_Race_Proof_Rejects_An_Unlocked_Parent_Lookup_With_The_Foreign_Key_Intact(string table)
    {
        var proof = async () => await ProveParentLookupLockAsync(table, removeLookupLock: true);
        await proof.Should().ThrowAsync<XunitException>()
            .WithMessage("*explicit lookup lock before its FK check*");
    }

    private async Task ProveParentLookupLockAsync(string table, bool removeLookupLock)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var parent = await CreateEmptyParentAsync(database.AppConnectionString, table);
        if (removeLookupLock)
        {
            await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
            var original = (await SchemaQueries.ReadStringsAsync(owner,
                $"SELECT pg_get_functiondef('public.fn_{table}_parent_scope()'::regprocedure)")).Single();
            var unlocked = original.Replace("FOR KEY SHARE", "", StringComparison.Ordinal);
            unlocked.Should().NotBe(original, "the control must actually remove the applied lookup lock");
            await SchemaQueries.ExecuteAsync(owner, null, unlocked);
        }

        const long gateKey = 714202609;
        await SetupAsync(database,
            $"""
            GRANT DELETE ON public.{ParentTable(table)} TO learnstack_app;
            CREATE FUNCTION public.fn_education_parent_test_gate() RETURNS trigger
                LANGUAGE plpgsql SECURITY INVOKER SET search_path = pg_catalog AS $$
            BEGIN
                PERFORM pg_catalog.pg_advisory_xact_lock({gateKey});
                RETURN NEW;
            END;
            $$;
            CREATE TRIGGER zz_education_parent_test_gate BEFORE INSERT ON public.{table}
                FOR EACH ROW EXECUTE FUNCTION public.fn_education_parent_test_gate();
            """);

        await using var gate = await OpenAppAsync(database.AppConnectionString);
        await using var child = await OpenAppAsync(database.AppConnectionString);
        await using var deletion = await OpenAppAsync(database.AppConnectionString);
        await using var observer = await OpenAppAsync(database.AppConnectionString);
        await using var gateTransaction = await gate.BeginTransactionAsync();
        await using var childTransaction = await child.BeginTransactionAsync();
        await using var deleteTransaction = await deletion.BeginTransactionAsync();
        await AnnounceAsync(gate, gateTransaction);
        await AnnounceAsync(child, childTransaction);
        await AnnounceAsync(deletion, deleteTransaction);
        await SchemaQueries.ExecuteAsync(gate, gateTransaction, $"SELECT pg_advisory_xact_lock({gateKey})");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var childId = Guid.NewGuid();
        await using var insert = InsertCommand(child, childTransaction, table, childId, parent);
        await using var delete = new NpgsqlCommand(
            $"DELETE FROM public.{ParentTable(table)} WHERE id = @id", deletion, deleteTransaction);
        delete.Parameters.AddWithValue("id", parent);
        Task<int>? childInsert = null;
        Task<int>? parentDelete = null;
        var gateReleased = false;
        try
        {
            childInsert = insert.ExecuteNonQueryAsync(timeout.Token);
            await WaitForBlockAsync(observer, child.ProcessID, gate.ProcessID, childInsert,
                "the child must reach the test gate after its parent-scope trigger", timeout.Token);

            parentDelete = delete.ExecuteNonQueryAsync(timeout.Token);
            await WaitForBlockAsync(observer, deletion.ProcessID, child.ProcessID, parentDelete,
                "the parent DELETE must wait on the child's explicit lookup lock before its FK check", timeout.Token);
            (await IsBlockedAsync(observer, child.ProcessID, gate.ProcessID, timeout.Token)).Should().BeTrue(
                "the child is still inside BEFORE INSERT; an ordinary FK lock cannot satisfy this proof");

            await gateTransaction.RollbackAsync(timeout.Token);
            gateReleased = true;
            (await childInsert).Should().Be(1);
            await childTransaction.CommitAsync(timeout.Token);

            if (table == "lessons")
            {
                var finishDelete = async () => await parentDelete;
                var refusal = (await finishDelete.Should().ThrowAsync<PostgresException>()).Which;
                refusal.SqlState.Should().Be(PostgresErrorCodes.RestrictViolation);
                refusal.ConstraintName.Should().Be("fk_lessons_course");
                await deleteTransaction.RollbackAsync(timeout.Token);
            }
            else
            {
                (await parentDelete).Should().Be(1,
                    "satellite foreign keys deliberately CASCADE after the child transaction releases its lock");
                await deleteTransaction.CommitAsync(timeout.Token);
            }

            await using var verification = await observer.BeginTransactionAsync(timeout.Token);
            await AnnounceAsync(observer, verification);
            var expected = table == "lessons" ? 1L : 0L;
            (await SchemaQueries.CountAsync(observer,
                $"SELECT count(*) FROM public.{ParentTable(table)} WHERE id = '{parent}'", verification))
                .Should().Be(expected);
            (await SchemaQueries.CountAsync(observer,
                $"SELECT count(*) FROM public.{table} WHERE {ParentColumn(table)} = '{parent}'", verification))
                .Should().Be(expected, "RESTRICT preserves both rows; CASCADE removes both rows");
        }
        finally
        {
            // Release the gate even on failed assertions, then cancel and observe
            // every in-flight command before await-using rolls back its transaction.
            // Cancellation has Npgsql's bounded cancellation timeout; no cleanup
            // command is issued on a connection still running another command.
            if (!gateReleased)
            {
                await gateTransaction.RollbackAsync();
            }

            await timeout.CancelAsync();
            foreach (var task in new[] { childInsert, parentDelete }.OfType<Task<int>>())
            {
                try
                {
                    await task;
                }
                catch (Exception exception) when (exception is NpgsqlException or OperationCanceledException)
                {
                    // The proof above owns the assertion; cleanup observes pending errors.
                }
            }
        }
    }

    private static async Task WaitForBlockAsync(NpgsqlConnection observer, int waiter, int blocker,
        Task operation, string because, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            if (await IsBlockedAsync(observer, waiter, blocker, cancellationToken))
            {
                return;
            }

            operation.IsCompleted.Should().BeFalse(because);
            // Poll only while waiting for an observable lock edge, with a deadline.
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
        }

        Assert.Fail($"Timed out observing backend {waiter} blocked by {blocker}: {because}.");
    }

    private static async Task<bool> IsBlockedAsync(NpgsqlConnection observer,
        int waiter, int blocker, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT @blocker = ANY(pg_blocking_pids(@waiter))", observer);
        command.Parameters.AddWithValue("waiter", waiter);
        command.Parameters.AddWithValue("blocker", blocker);
        return (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static async Task<Guid> CreateEmptyParentAsync(string connectionString, string childTable)
    {
        await using var app = await OpenAppAsync(connectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await AnnounceAsync(app, transaction);
        var course = Guid.NewGuid();
        await EducationSchemaSeed.InsertAsync(app, transaction, "courses",
            SchemaFixture.TenantA, SchemaFixture.OrgA1, course, course);
        var parent = course;
        if (childTable == "lesson_translations")
        {
            parent = Guid.NewGuid();
            await EducationSchemaSeed.InsertAsync(app, transaction, "lessons",
                SchemaFixture.TenantA, SchemaFixture.OrgA1, parent, course);
        }

        await transaction.CommitAsync();
        return parent;
    }

    private static async Task<int> ReparentAsync(NpgsqlConnection app, NpgsqlTransaction transaction,
        string table, Guid key, Guid parent)
    {
        var rowKey = table == "lessons" ? "id" : ParentColumn(table);
        await using var command = new NpgsqlCommand(
            $"UPDATE public.{table} SET {ParentColumn(table)} = @parent WHERE {rowKey} = @id"
            + (table == "lessons" ? "" : " AND locale = 'en'"), app, transaction);
        command.Parameters.AddWithValue("parent", parent);
        command.Parameters.AddWithValue("id", key);
        return await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlCommand InsertCommand(NpgsqlConnection app, NpgsqlTransaction transaction,
        string table, Guid id, Guid parent)
    {
        var command = new NpgsqlCommand(EducationSchemaSeed.InsertSql(table), app, transaction);
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        command.Parameters.AddWithValue("organization", SchemaFixture.OrgA1);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("parent", parent);
        command.Parameters.AddWithValue("slug", "race-" + id.ToString("N"));
        command.Parameters.AddWithValue("locale", "en");
        command.Parameters.AddWithValue("actor", SchemaFixture.Actor);
        return command;
    }

    private static Task AnnounceAsync(NpgsqlConnection app, NpgsqlTransaction transaction) =>
        EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);

    private static string ParentTable(string table) => table == "lesson_translations" ? "lessons" : "courses";
    private static string ParentColumn(string table) => table == "lesson_translations" ? "lesson_id" : "course_id";
    private static Guid ParentId(string table, EducationSchemaSeed.Scope row) =>
        table == "lesson_translations" ? row.LessonId : row.CourseId;

    private static async Task SetupAsync(DisposableSchemaDatabase database, string sql)
    {
        await using var owner = await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        await SchemaQueries.ExecuteAsync(owner, null, sql);
    }

    private static async Task<NpgsqlConnection> OpenAppAsync(string connectionString)
    {
        var app = new NpgsqlConnection(connectionString);
        try
        {
            await app.OpenAsync();
            (await SchemaQueries.ReadStringsAsync(app,
                "SELECT session_user || '/' || current_user FROM pg_roles "
                + "WHERE rolname = current_user AND NOT rolsuper AND NOT rolbypassrls"))
                .Should().Equal("learnstack_app/learnstack_app");
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }
}
