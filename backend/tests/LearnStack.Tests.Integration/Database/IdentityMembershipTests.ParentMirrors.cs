using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Permissions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    [Theory]
    [InlineData("profile", "missing")]
    [InlineData("profile", "hidden")]
    [InlineData("profile", "mismatched")]
    [InlineData("assignment", "missing")]
    [InlineData("assignment", "hidden")]
    [InlineData("assignment", "mismatched")]
    public async Task Complete_child_trigger_stack_has_one_parent_scope_refusal(string child, string parent)
    {
        var failure = await ParentMirrorStackFailureAsync(fixture.Postgres.AppConnectionString, child, parent);
        failure.SqlState.Should().Be("23514");
        failure.ConstraintName.Should().Be("membership_parent_scope");
        failure.MessageText.Should().Be("identity graph is invalid");
        failure.Detail.Should().BeNull();
    }

    private static async Task<PostgresException> ParentMirrorStackFailureAsync(string appConnection,
        string child, string parent)
    {
        var member = parent switch
        {
            "missing" => Guid.CreateVersion7(),
            "hidden" => BranchMembershipA2,
            "mismatched" => LearnerMembershipA,
            _ => throw new ArgumentOutOfRangeException(nameof(parent)),
        };
        if (parent == "hidden")
        {
            await using var visible = (NpgsqlConnection)await PostgresFixture.OpenAsync(appConnection);
            await SchemaQueries.AssertApplicationRoleAsync(visible);
            await using var visibleTransaction = await visible.BeginTransactionAsync();
            await ScopeAsync(visible, visibleTransaction, SchemaFixture.TenantA, SchemaFixture.OrgA2);
            (await ScalarAsync<long>(visible, visibleTransaction,
                "SELECT count(*) FROM public.memberships WHERE id=@member", ("member", member))).Should().Be(1,
                "the hidden parent exists and is visible through its own authenticated app-role organization scope");
        }

        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(appConnection);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, SchemaFixture.TenantA, SchemaFixture.OrgA1);
        await LockAsync(app, transaction, SchemaFixture.TenantA, PersonA, false);
        (await ScalarAsync<long>(app, transaction,
            "SELECT count(*) FROM public.memberships WHERE id=@member", ("member", member))).Should()
            .Be(parent == "mismatched" ? 1 : 0);

        var role = RoleId(SchemaFixture.TenantA, BuiltInRoleKey.OrgInstructor, PermissionScope.Organization);
        if (child == "profile")
        {
            await ExecuteAsync(app, transaction,
                "UPDATE public.membership_profiles SET display_name='parent-mirror-control' WHERE membership_id=@member",
                ("member", BranchMembershipA1));
            (await ScalarAsync<long>(app, transaction,
                "SELECT count(*) FROM public.membership_profiles WHERE membership_id=@member AND display_name='parent-mirror-control'",
                ("member", BranchMembershipA1))).Should().Be(1);
        }
        else
        {
            child.Should().Be("assignment");
            await ExecuteAsync(app, transaction,
                "INSERT INTO public.membership_role_assignments(tenant_id,membership_id,organization_id,role_id) VALUES(@tenant,@member,@organization,@role)",
                ("tenant", SchemaFixture.TenantA), ("member", BranchMembershipA1),
                ("organization", SchemaFixture.OrgA1), ("role", role));
            (await ScalarAsync<long>(app, transaction,
                "SELECT count(*) FROM public.membership_role_assignments WHERE membership_id=@member AND role_id=@role",
                ("member", BranchMembershipA1), ("role", role))).Should().Be(1);
        }
        await ExecuteAsync(app, transaction, "SET CONSTRAINTS ALL IMMEDIATE");
        var sql = child == "profile"
            ? "INSERT INTO public.membership_profiles(tenant_id,membership_id,organization_id) VALUES(@tenant,@member,@organization)"
            : "INSERT INTO public.membership_role_assignments(tenant_id,membership_id,organization_id,role_id) VALUES(@tenant,@member,@organization,@role)";
        var write = () => ExecuteAsync(app, transaction, sql,
            ("tenant", SchemaFixture.TenantA), ("member", member),
            ("organization", SchemaFixture.OrgA1), ("role", role));
        return (await write.Should().ThrowAsync<PostgresException>()).Which;
    }
}
