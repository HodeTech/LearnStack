using System.Reflection;
using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The closure's last-administrator read on the platform connection against the administrator predicate the deferred
/// complete-graph guard enforces, over the same fixtures (ADR-0107 § 7). The guard is asked by the only question it
/// answers: a Membership write in the tenant commits only while an eligible tenant-wide administrator remains. So, with
/// exactly two candidate people in tenant B, revoking the subject's Memberships passes the guard exactly when the read says
/// somebody else is eligible, and revoking the other's passes exactly when it says the subject is.
/// </summary>
/// <remarks>
/// The application role's read of the same predicate (<c>DataRequestStore</c>) cannot see another person's User status;
/// it agrees with the platform read in every fixture but the one whose other administrator's User is Closing, which is the
/// one condition the platform read adds.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AccountClosureAdministratorParityTests(SchemaFixture schema)
{
    private static readonly Guid Tenant = SchemaFixture.TenantB;

    public enum Fixture
    {
        ActiveAdministrator,
        SuspendedAdministrator,
        RevokedAdministrator,
        ClosingAdministrator,
        OrganizationAdministrator,
        TenantWideLearner,
        KernelGrantMissing,
        SubjectIsALearner,
    }

    [Theory]
    [InlineData(Fixture.ActiveAdministrator, true, true)]
    [InlineData(Fixture.SuspendedAdministrator, true, false)]
    [InlineData(Fixture.RevokedAdministrator, true, false)]
    [InlineData(Fixture.ClosingAdministrator, true, false)]
    [InlineData(Fixture.OrganizationAdministrator, true, false)]
    [InlineData(Fixture.TenantWideLearner, true, false)]
    [InlineData(Fixture.KernelGrantMissing, false, false)]
    [InlineData(Fixture.SubjectIsALearner, false, true)]
    public async Task The_platform_read_answers_as_the_complete_graph_guard_for_every_administrator_state(
        Fixture fixture, bool subjectEligible, bool otherEligible)
    {
        await using var kit = await AccountClosureBeginKit.CreateAsync(schema.Postgres);
        var (subject, other) = await ArrangeAsync(kit, fixture);

        var platform = await PlatformStandingAsync(kit, subject);
        platform.Should().Be((subjectEligible, otherEligible), "the platform read over " + fixture);

        (await RevocationCommitsAsync(kit, subject)).Should().Be(otherEligible,
            "the guard lets the subject go exactly when somebody else is eligible: " + fixture);
        if (fixture != Fixture.RevokedAdministrator)
        {
            (await RevocationCommitsAsync(kit, other)).Should().Be(subjectEligible,
                "the guard lets the other go exactly when the subject is eligible: " + fixture);
        }

        var application = await ApplicationStandingAsync(kit, subject);
        application.Should().Be(fixture == Fixture.ClosingAdministrator ? (true, true) : platform,
            "the application role's read differs only where it cannot see a User's status");
    }

    /// <summary>The subject and the one other candidate of tenant B, in the fixture's state.</summary>
    private static async Task<(Guid Subject, Guid Other)> ArrangeAsync(AccountClosureBeginKit kit, Fixture fixture)
    {
        if (fixture == Fixture.SubjectIsALearner)
        {
            // Tenant B's seeded people: its administrator and a branch learner.
            return (PersonB, AdminB);
        }

        var other = await kit.PersonAsync();
        var membership = fixture switch
        {
            Fixture.OrganizationAdministrator => await kit.MemberAsync(Tenant, SchemaFixture.OrgB1, other, BuiltInRoleKey.OrgAdmin),
            Fixture.TenantWideLearner => await kit.MemberAsync(Tenant, null, other, BuiltInRoleKey.Learner),
            _ => await kit.MemberAsync(Tenant, null, other, BuiltInRoleKey.TenantAdmin),
        };
        switch (fixture)
        {
            case Fixture.SuspendedAdministrator:
                await using (var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString))
                await using (var transaction = await app.BeginTransactionAsync())
                {
                    await ScopeAsync(app, transaction, Tenant);
                    await LockAsync(app, transaction, Tenant, other, exclusive: true);
                    await ExecuteAsync(app, transaction, "UPDATE public.memberships SET status='Suspended' WHERE id=@id", ("id", membership));
                    await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
                    await transaction.CommitAsync();
                }

                break;
            case Fixture.RevokedAdministrator:
                await AccountClosureBeginTests.RevokeAsync(kit, Tenant, null, other, membership);
                break;
            case Fixture.ClosingAdministrator:
                // The closure's own writer: the platform session under the person's and the tenant's fences.
                await using (var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString))
                await using (var transaction = await platform.BeginTransactionAsync())
                {
                    await ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)", ("key", AccountClosureBeginKit.UserKey(other)));
                    await ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)", ("key", AccountClosureBeginKit.TenantKey(Tenant)));
                    await ExecuteAsync(platform, transaction, "UPDATE public.users SET status='Closing' WHERE id=@user", ("user", other));
                    await transaction.CommitAsync();
                }

                break;
            case Fixture.KernelGrantMissing:
                // No product path reaches it, because the guard refuses the grant's removal; a superuser fixture does.
                await kit.SqlAsync($"""
                    SET session_replication_role = replica;
                    DELETE FROM public.role_permission_grants WHERE tenant_id = '{Tenant}'
                      AND role_id = '{RoleId(Tenant, BuiltInRoleKey.TenantAdmin, PermissionScope.Tenant)}'
                      AND permission_key = '{IdentityFoundationManifest.RoleAdmin}';
                    """);
                break;
        }

        return (AdminB, other);
    }

    private static async Task<(bool Subject, bool Other)> PlatformStandingAsync(AccountClosureBeginKit kit, Guid subject)
    {
        await using var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand(AccountClosureOperatorOperations.AdministratorStandingSql, platform);
        command.Parameters.AddWithValue("subject", subject);
        command.Parameters.AddWithValue("tenants", new[] { Tenant });
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return (false, false);
        }

        reader.GetGuid(0).Should().Be(Tenant);
        var standing = (reader.GetBoolean(1), reader.GetBoolean(2));
        (await reader.ReadAsync()).Should().BeFalse("one row per tenant");
        return standing;
    }

    private static async Task<(bool Subject, bool Other)> ApplicationStandingAsync(AccountClosureBeginKit kit, Guid subject)
    {
        var sql = (string)typeof(DataRequestStore).GetField("AdministratorStanding", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        await using var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, Tenant);
        await using var command = new NpgsqlCommand(sql, app, transaction);
        command.Parameters.AddWithValue("tenant", Tenant);
        command.Parameters.AddWithValue("subject", subject);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return (reader.GetBoolean(0), reader.GetBoolean(1));
    }

    /// <summary>
    /// Whether revoking every live Membership the person holds in tenant B commits past the complete-graph guard, which runs
    /// at once here; the probe always rolls back.
    /// </summary>
    private static async Task<bool> RevocationCommitsAsync(AccountClosureBeginKit kit, Guid person)
    {
        var live = new List<(Guid Membership, Guid? Organization)>();
        foreach (var organization in new Guid?[] { null, SchemaFixture.OrgB1 })
            foreach (var membership in await LiveMembershipsAsync(kit, person, organization))
                live.Add((membership, organization));
        live.Should().NotBeEmpty("the probe revokes something");

        await using var app = await DataExportTestKit.OpenAppAsync(kit.Database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await LockAsync(app, transaction, Tenant, person, exclusive: true);
        try
        {
            foreach (var (membership, organization) in live)
            {
                await ScopeAsync(app, transaction, Tenant, organization);
                await ExecuteAsync(app, transaction, """
                    UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@system,
                        updated_at=clock_timestamp(), updated_by=@system, row_version=row_version+1
                    WHERE id=@member
                    """, ("member", membership), ("system", UserId.SystemActor.Value));
            }

            await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            return true;
        }
        catch (PostgresException refusal) when (refusal.SqlState == "P0303")
        {
            return false;
        }
        finally
        {
            await transaction.RollbackAsync();
        }
    }

    private static async Task<IReadOnlyList<Guid>> LiveMembershipsAsync(AccountClosureBeginKit kit, Guid person, Guid? organization)
    {
        await using var platform = await AccountClosureBeginKit.OpenAsync(kit.Database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT id FROM public.memberships WHERE tenant_id=@tenant AND user_id=@user
              AND organization_id IS NOT DISTINCT FROM @organization AND status <> 'Revoked'
            """, platform);
        command.Parameters.AddWithValue("tenant", Tenant);
        command.Parameters.AddWithValue("user", person);
        command.Parameters.Add(new NpgsqlParameter("organization", NpgsqlTypes.NpgsqlDbType.Uuid) { Value = (object?)organization ?? DBNull.Value });
        await using var reader = await command.ExecuteReaderAsync();
        var ids = new List<Guid>();
        while (await reader.ReadAsync()) ids.Add(reader.GetGuid(0));
        return ids;
    }
}
