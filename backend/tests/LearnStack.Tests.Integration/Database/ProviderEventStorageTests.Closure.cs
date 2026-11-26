using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0097's intake table and ADR-0107 § 7 ("a person cannot be resurrected by ... provider intake"): a fact about a
/// person whose User is Closing, or Closed, is a durable receipt only, <c>subject_closed</c>, with no tenant target, so no
/// mirror is ever produced for it. The same person's fact while Active is the control: it routes to the person's tenant.
/// </summary>
public sealed partial class ProviderEventStorageTests
{
    [Theory]
    [InlineData("Closing")]
    [InlineData("Closed")]
    public async Task A_fact_about_a_closing_or_closed_User_is_receipt_only_subject_closed_without_a_target(string status)
    {
        // A person of this case alone, so the shared fixture's people are untouched.
        var person = Guid.CreateVersion7();
        await IdentityMembershipFixture.InsertUserAsync(fixture.Postgres.AppConnectionString, person);
        await using (var app = await OpenAsync(fixture.Postgres.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await IdentityMembershipFixture.ScopeAsync(app, transaction, SchemaFixture.TenantA);
            await IdentityMembershipFixture.LockAsync(app, transaction, SchemaFixture.TenantA, person, false);
            await IdentityMembershipFixture.InsertMemberAsync(app, transaction, SchemaFixture.TenantA, null, Guid.CreateVersion7(), person,
                IdentityMembershipFixture.RoleId(SchemaFixture.TenantA, BuiltInRoleKey.Learner, PermissionScope.Tenant));
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
            await transaction.CommitAsync();
        }

        var active = await AcceptAsync(Guid.CreateVersion7(), "LOGIN", SubjectKey(person));
        (active.Accepted, active.Disposition, active.Count).Should().Be((true, "routed", 1), "the Active person's fact reaches the tenant");

        // The closure's transitions, on the platform role under the User key and the person's one participant tenant key.
        foreach (var sql in status == "Closed"
                     ? new[] { "UPDATE public.users SET status='Closing' WHERE id=@user",
                         "UPDATE public.users SET status='Closed', provider_subject=NULL WHERE id=@user" }
                     : ["UPDATE public.users SET status='Closing' WHERE id=@user"])
        {
            await using var platform = await OpenAsync(fixture.Postgres.PlatformConnectionString);
            await using var transaction = await platform.BeginTransactionAsync();
            await IdentityMembershipFixture.ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForUser(UserId.From(person))));
            await IdentityMembershipFixture.ExecuteAsync(platform, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForTenant(TenantId.From(SchemaFixture.TenantA))));
            await IdentityMembershipFixture.ExecuteAsync(platform, transaction, sql, ("user", person));
            await transaction.CommitAsync();
        }

        var fact = Guid.CreateVersion7();
        var refused = await AcceptAsync(fact, "LOGIN", SubjectKey(person));

        (refused.Accepted, refused.Disposition, refused.Count).Should().Be((true, "subject_closed", 0),
            "a closing or closed person's fact is a durable receipt with no target");
        (await AcceptAsync(fact, "LOGIN", SubjectKey(person), refused.At)).Should().Be(refused, "the receipt is the fact's one answer");
        await using var reader = await OpenAsync(fixture.Postgres.AppConnectionString);
        await using var read = await reader.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(reader, read, SchemaFixture.TenantA);
        (await IdentityMembershipFixture.ScalarAsync<long>(reader, read,
            "SELECT count(*) FROM public.identity_provider_event_targets WHERE provider_event_id=@id", ("id", fact))).Should().Be(0);
    }
}
