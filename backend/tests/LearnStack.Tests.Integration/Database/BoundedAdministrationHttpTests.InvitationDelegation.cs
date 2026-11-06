using System.Net;
using System.Text.Json;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Authorization;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Theory]
    [InlineData("create")]
    [InlineData("reissue")]
    [InlineData("directory")]
    public async Task Actual_invitation_delegation_unavailable_aborts_instead_of_denial_or_partial_success(string operation)
    {
        await using var proof = await CreatePreparedAsync();
        using var normal = proof.Client();
        var token = proof.Token(EnglishAdmin, Tenant);
        using var choices = await SendAsync(normal, token, "/api/v1/identity/invitable-roles");
        var available = (await JsonAsync(choices)).GetProperty("items");
        var role = available.EnumerateArray().Single(row => row.GetProperty("builtInKey").GetString() == "Learner");
        string Body(string email) => JsonSerializer.Serialize(new
        {
            email,
            roleId = role.GetProperty("roleId").GetString(),
            roleVersion = role.GetProperty("version").GetString()
        });
        using var baseline = await SendAsync(normal, token, "/api/v1/identity/invitations", "POST", Body("baseline-delegation@example.test"), key: Key());
        var version = (await JsonAsync(baseline, HttpStatusCode.Created)).GetProperty("version").GetString()!;
        var location = baseline.Headers.Location!.OriginalString;
        var sequence = new InvitationDelegationSequence();
        using var faulted = proof.Client(services => services.Replace(ServiceDescriptor.Scoped<IIdentityAuthorizationReader>(provider =>
            new SequencedInvitationDelegationReader(provider.GetRequiredService<CommittedIdentityAuthorizationReader>(),
                provider.GetRequiredService<IOperationInvocationContext>(), sequence))));
        var path = operation == "directory" ? "/api/v1/identity/invitable-roles"
            : operation == "reissue" ? location + "/reissues" : "/api/v1/identity/invitations";
        using var failed = await SendAsync(faulted, token, path, operation == "directory" ? "GET" : "POST",
            operation == "directory" ? null : operation == "reissue" ? "{}" : Body("unavailable-delegation@example.test"),
            operation == "reissue" ? version : null, operation == "directory" ? null : Key());
        await ProblemAsync(failed, HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
        Assert.Equal(4, sequence.Reads.Count);
        Assert.All(sequence.Reads.Take(3), outcome => Assert.Equal(IdentityAuthorizationOutcome.Covered, outcome));
        Assert.Equal(IdentityAuthorizationOutcome.Unavailable, sequence.Reads[3]);
        Assert.DoesNotContain(await proof.AuditAsync(failed, Tenant), row => row.Outcome == "success");
        Assert.DoesNotContain("\"items\"", await failed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using (var unchanged = await SendAsync(normal, token, location))
            Assert.Equal(version, (await JsonAsync(unchanged)).GetProperty("version").GetString());

        // Only the delegation read fails once; all later facts recover. A final
        // successful recheck must never turn that earlier failure into directory data.
        using var recovered = await SendAsync(faulted, token, "/api/v1/identity/invitable-roles");
        var recoveredItems = (await JsonAsync(recovered)).GetProperty("items");
        Assert.Equal(available.GetRawText(), recoveredItems.GetRawText());
        Assert.All(sequence.Reads.Skip(4), outcome => Assert.Equal(IdentityAuthorizationOutcome.Covered, outcome));
        Assert.True(sequence.Reads.Count > 4);
        Assert.Contains(await proof.AuditAsync(recovered, Tenant), row => row.Operation == "identity.invitation.list_invitable_roles" && row.Outcome == "success");
    }

    [Fact]
    public async Task Actual_insufficient_delegation_still_denies_mutations_and_omits_only_uncovered_roles()
    {
        await using var proof = await CreatePreparedAsync();
        using var client = proof.Client();
        var token = proof.Token(EnglishAdmin, Tenant);
        using var choices = await SendAsync(client, token, "/api/v1/identity/invitable-roles");
        var learner = (await JsonAsync(choices)).GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("builtInKey").GetString() == "Learner");
        string Body(string email) => JsonSerializer.Serialize(new
        {
            email,
            roleId = learner.GetProperty("roleId").GetString(),
            roleVersion = learner.GetProperty("version").GetString()
        });
        using var created = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", Body("before-grant-loss@example.test"), key: Key());
        var version = (await JsonAsync(created, HttpStatusCode.Created)).GetProperty("version").GetString()!;
        var location = created.Headers.Location!.OriginalString;
        var admin = SeedData.IdentityRoles.Single(row => row.Tenant == Tenant && row.BuiltInKey == BuiltInRoleKey.TenantAdmin);
        var rolePath = "/api/v1/identity/roles/" + admin.RoleId.ToString("D");
        using (var detail = await SendAsync(client, token, rolePath))
        {
            var current = await JsonAsync(detail);
            var retained = current.GetProperty("grants").EnumerateArray()
                .Where(grant => grant.GetProperty("key").GetString() != "identity.membership_profile.write")
                .Select(grant => new { scope = grant.GetProperty("scope").GetString(), key = grant.GetProperty("key").GetString() }).ToArray();
            await MutateAsync(client, token, "PUT", rolePath + "/grants", JsonSerializer.Serialize(new { grants = retained }),
                current.GetProperty("version").GetString()!, Key());
        }
        using (var denied = await SendAsync(client, token, "/api/v1/identity/invitations", "POST", Body("uncovered-delegation@example.test"), key: Key()))
            await ProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
        using (var denied = await SendAsync(client, token, location + "/reissues", "POST", "{}", version, Key()))
            await ProblemAsync(denied, HttpStatusCode.Forbidden, "forbidden");
        using var directory = await SendAsync(client, token, "/api/v1/identity/invitable-roles");
        var items = (await JsonAsync(directory)).GetProperty("items").EnumerateArray().ToArray();
        Assert.DoesNotContain(items, row => row.GetProperty("roleId").GetString() == learner.GetProperty("roleId").GetString());
        Assert.Contains(items, row => row.GetProperty("builtInKey").GetString() == "Instructor");
        Assert.Contains(await proof.AuditAsync(directory, Tenant), row => row.Operation == "identity.invitation.list_invitable_roles" && row.Outcome == "success");
    }

    private sealed class InvitationDelegationSequence
    {
        public List<IdentityAuthorizationOutcome> Reads { get; } = [];
        public bool Faulted { get; set; }
    }

    private sealed class SequencedInvitationDelegationReader(CommittedIdentityAuthorizationReader inner,
        IOperationInvocationContext invocations, InvitationDelegationSequence sequence) : IIdentityAuthorizationReader
    {
        private int _applicationReads;
        public async Task<IdentityAuthorizationResult> ReadAsync(AuthorizationCandidate candidate, CancellationToken cancellationToken = default)
        {
            var invocation = invocations.CurrentInvocation;
            var selected = invocation is
            {
                EntryKind: OperationEntryKind.Application,
                Operation: CreateInvitationCommand or ReissueInvitationCommand or ListInvitableRolesQuery
            };
            var current = await inner.ReadAsync(candidate, cancellationToken);
            if (!selected) return current;
            // Create/Reissue: initial policy, admission recheck, post-lock recheck,
            // then delegation. Directory: initial policy, admission, two Role reads.
            if (++_applicationReads == 4 && !sequence.Faulted)
            {
                Assert.Equal(IdentityAuthorizationOutcome.Covered, current.Outcome);
                sequence.Faulted = true;
                current = current with { Outcome = IdentityAuthorizationOutcome.Unavailable, Memberships = [] };
            }
            sequence.Reads.Add(current.Outcome);
            return current;
        }
    }
}
