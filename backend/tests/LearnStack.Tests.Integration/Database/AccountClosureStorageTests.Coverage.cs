using FluentAssertions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.DataRequestSchemaSeed;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class AccountClosureStorageTests
{
    /// <summary>
    /// The fences are the connection's own: keys that another backend holds, even every key the arrow needs, admit nothing
    /// for this backend. For the User move and for a closure's Begin, one backend holds some of the closure's fences and
    /// this one holds the rest or none, and the arrow names the missing fence it needs. With the fences held by this
    /// backend alone and no other holder, the same statements are admitted.
    /// </summary>
    [Fact]
    public async Task A_fence_another_backend_holds_admits_nothing_for_this_one()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        var arranged = await ArrangeAsync(database);

        // The User move, while the person is Active: what the other backend holds, and what this one takes beside it.
        foreach (var (held, own, reason, because) in new (Mode[] Held, Mode[] Own, string Reason, string Because)[]
        {
            ([Mode.Exclusive, Mode.Exclusive, Mode.Exclusive], [Mode.None, Mode.None, Mode.None], "User closure fence refused.",
                "another backend holds every fence and this one none"),
            ([Mode.Exclusive, Mode.None, Mode.None], [Mode.None, Mode.Exclusive, Mode.Exclusive], "User closure fence refused.",
                "another backend holds the User key and this one both tenant keys"),
            ([Mode.Exclusive, Mode.Exclusive, Mode.None], [Mode.None, Mode.None, Mode.Exclusive], "User closure fence refused.",
                "another backend holds the User key and one tenant key, this one the other tenant key"),
            ([Mode.None, Mode.Exclusive, Mode.Exclusive], [Mode.Exclusive, Mode.None, Mode.None], "User closure tenant fence refused.",
                "another backend holds both tenant keys and this one the User key"),
            ([Mode.None, Mode.None, Mode.Exclusive], [Mode.Exclusive, Mode.Exclusive, Mode.None], "User closure tenant fence refused.",
                "another backend holds the tombstone tenant's key and this one the rest"),
        })
        {
            await using var holder = await OpenAsync(database.PlatformConnectionString);
            await using var holding = await holder.BeginTransactionAsync();
            await PersonAFences(held[0], held[1], held[2])(holder, holding);
            (await HeldAdvisoryKeysAsync(database, holder)).Should().Be(held.Count(mode => mode != Mode.None), because);

            var refusal = await PlatformRefusedAsync(database, PersonAFences(own[0], own[1], own[2]), Close, ("user", PersonA));

            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), because);
        }

        (await UserStateAsync(database, PersonA)).Status.Should().Be("Active");
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));

        // A closure's Begin, once the person is Closing: the same, with the arrow's own two fence messages.
        foreach (var (held, own, reason, because) in new (Mode[] Held, Mode[] Own, string Reason, string Because)[]
        {
            ([Mode.Exclusive, Mode.Exclusive, Mode.Exclusive], [Mode.None, Mode.None, Mode.None], UserFence,
                "another backend holds every fence and this one none"),
            ([Mode.Exclusive, Mode.None, Mode.None], [Mode.None, Mode.Exclusive, Mode.Exclusive], UserFence,
                "another backend holds the User key and this one both tenant keys"),
            ([Mode.None, Mode.Exclusive, Mode.Exclusive], [Mode.Exclusive, Mode.None, Mode.None], TenantFence,
                "another backend holds both tenant keys and this one the User key"),
        })
        {
            await using var holder = await OpenAsync(database.PlatformConnectionString);
            await using var holding = await holder.BeginTransactionAsync();
            await PersonAFences(held[0], held[1], held[2])(holder, holding);
            (await HeldAdvisoryKeysAsync(database, holder)).Should().Be(held.Count(mode => mode != Mode.None), because);

            var refusal = await PlatformRefusedAsync(database, PersonAFences(own[0], own[1], own[2]), Format(Begin), ("id", arranged.DueClosureA));

            (refusal.SqlState, refusal.MessageText).Should().Be(("23514", reason), because);
        }

        await PlatformCommitAsync(database, FullFencesOfPersonA, Format(Begin), arranged.DueClosureA);
        (await RequestStateAsync(database, SchemaFixture.TenantA, null, arranged.DueClosureA)).Status.Should().Be("fulfilling",
            "this backend's own fences admit the Begin once no other backend is involved");
    }

    /// <summary>
    /// A fan-out erasure is completed only for a subject who is Closing or Closed. For a subject that is Active, which no
    /// admitted path produces and the owner forces here, the arrow is refused even with both fences held, and admitted again
    /// once the subject is Closing.
    /// </summary>
    [Fact]
    public async Task A_fan_out_erasure_is_not_completed_for_a_subject_that_is_Active()
    {
        await using var database = await DataExportTestKit.CreateSeededDatabaseAsync(schema.Postgres);
        await ArrangeAsync(database);
        await MoveAsync(database, PersonA, Close, (SchemaFixture.TenantA, Mode.Exclusive), (SchemaFixture.TenantB, Mode.Exclusive));
        var fan = await FanOutAsync(database, SchemaFixture.TenantA, null, PersonA, LearnerMembershipA);
        var databaseName = new NpgsqlConnectionStringBuilder(database.MigrationConnectionString).Database;
        const string Redacted = "result='{\"consent_evidence\":2,\"audit_actor_reference\":4}'";
        Task Fences(NpgsqlConnection connection, NpgsqlTransaction transaction) =>
            ClosureFencesAsync(connection, transaction, PersonA, Mode.Exclusive, (SchemaFixture.TenantA, Mode.Exclusive));

        await schema.Postgres.ExecuteAsSuperuserAsync(
            $"SET session_replication_role = replica; UPDATE public.users SET status = 'Active' WHERE id = '{PersonA:D}'", databaseName);
        var refusal = await PlatformRefusedAsync(database, Fences, Format(Finalize + "," + Redacted), ("id", fan));
        (refusal.SqlState, refusal.MessageText).Should().Be(("23514", Subject), "the subject is Active, which is neither Closing nor Closed");

        await schema.Postgres.ExecuteAsSuperuserAsync(
            $"SET session_replication_role = replica; UPDATE public.users SET status = 'Closing' WHERE id = '{PersonA:D}'", databaseName);
        await PlatformAdmittedAsync(database, Fences, Format(Finalize + "," + Redacted), fan);
    }

    /// <summary>The advisory keys another backend holds, granted, by its process.</summary>
    private static async Task<long> HeldAdvisoryKeysAsync(DisposableSchemaDatabase database, NpgsqlConnection holder)
    {
        await using var observer = await OpenAsync(database.MigrationConnectionString);
        return await ScalarAsync<long>(observer, null,
            "SELECT count(*) FROM pg_catalog.pg_locks WHERE pid = @pid AND locktype = 'advisory' AND granted", ("pid", holder.ProcessID));
    }
}
