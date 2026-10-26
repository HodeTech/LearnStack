using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Last_administrator_guard_requires_global_eligibility_not_only_an_active_membership(bool eligible)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.MigrationConnectionString);
        var original = await UserControlCensusAsync(owner);
        var check = await ScalarAsync<string>(owner, null,
            "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'");
        try
        {
            if (!eligible)
            {
                // Fixture-confined closure; no production lifecycle grant or
                // owner-function is added. Restore exact controls before the app act.
                await using var close = await owner.BeginTransactionAsync();
                await ExecuteAsync(owner, close, $"""
                    SELECT pg_advisory_xact_lock(@user_key);
                    SELECT pg_advisory_xact_lock(@tenant_key);
                    ALTER TABLE public.users DISABLE TRIGGER tg_users_no_update;
                    ALTER POLICY users_isolation ON public.users WITH CHECK (({check}) OR
                        (current_user='learnstack_migration' AND id='{AdminA:D}'::uuid AND status='Closing'
                         AND deleted_at IS NULL AND deleted_by IS NULL));
                    UPDATE public.users SET status='Closing' WHERE id='{AdminA:D}'::uuid;
                    ALTER POLICY users_isolation ON public.users WITH CHECK ({check});
                    ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update;
                    """, ("user_key", IdentityAdvisoryLockKey.ForUser(UserId.From(AdminA))),
                    ("tenant_key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(SchemaFixture.TenantA))));
                await close.CommitAsync();
            }
            (await UserControlCensusAsync(owner)).Should().Be(original);
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction, SchemaFixture.TenantA);
            await LockAsync(app, transaction, SchemaFixture.TenantA, null, false);
            await ExecuteAsync(app, transaction,
                "UPDATE public.membership_profiles SET display_name='eligibility-control' WHERE membership_id=@id",
                ("id", LearnerMembershipA));
            var finish = () => ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            if (eligible)
            {
                await finish();
                await transaction.CommitAsync();
            }
            else
            {
                (await finish.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0303");
                await transaction.RollbackAsync();
            }
        }
        finally
        {
            await ExecuteAsync(owner, null,
                $"ALTER POLICY users_isolation ON public.users WITH CHECK ({check}); ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update");
        }
        (await UserControlCensusAsync(owner)).Should().Be(original);
    }

    [Fact]
    public async Task Direct_admission_function_returns_the_same_false_for_known_and_unknown_users()
    {
        await using var app = await OpenAsync();
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA);
        await LockAsync(app, transaction, SchemaFixture.TenantA, null, true);
        foreach (var user in new[] { AdminA, Guid.CreateVersion7() })
            (await ScalarAsync<bool>(app, transaction,
                "SELECT public.fn_identity_admin_admission_state(@user,@tenant)",
                ("user", user), ("tenant", SchemaFixture.TenantA))).Should().BeFalse();
    }
}
