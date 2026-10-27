using System.Net;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.Tools.Seeder;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class IdentityAuthorizationHttpTests(PublicReadFixture parent)
{
    [Fact]
    public async Task Actual_directory_resolves_rows_6_7_9_10_14_with_real_memberships_and_committed_sensitive_evidence()
    {
        await using var proof = await CreateAsync(parent);
        var claimActor = UserId.From(Guid.CreateVersion7());
        const string ClaimSubject = "directory-only-confirmed-subject";
        await proof.Database.AdoptAsync(claimActor, ClaimSubject);
        await proof.Database.AddMembershipAsync(SeedData.English, claimActor, role: BuiltInRoleKey.TenantAdmin);
        using var client = proof.Client();
        foreach (var row in new[] { 6, 7, 9, 10, 14 })
        {
            var tenant = row is 9 or 10 ? SeedData.Yoga : SeedData.English;
            var actor = row == 14 ? claimActor : row is 9 or 10 ? YogaAdmin : EnglishAdmin;
            var organization = row is 7 or 9;
            var token = proof.Token(actor, tenant, organization, row == 14 ? ClaimSubject : null);
            using var response = await SendAsync(client, token, row == 14 ? "localhost" : tenant.Host);
            var page = await PageAsync(response);
            page.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
            var audit = (await proof.AuditAsync(response, tenant,
                row is 7 or 9 or 10 ? tenant.DefaultOrganization.OrganizationId : null))
                .Should().ContainSingle().Subject;
            audit.Operation.Should().Be("identity.membership.list");
            audit.Outcome.Should().Be("success");
            audit.Actor.Should().Be(actor.Value);
            audit.Metadata.Should().Contain("ListMemberships");
        }
        proof.Observation.DirectoryReads.Should().Be(5);
        proof.Observation.AuthorizationReads.Should().BeGreaterThanOrEqualTo(10,
            "HTTP and application admission independently read committed authority");
        proof.Issuer.MetadataRequests.Should().Be(1);
        proof.Issuer.JwksRequests.Should().Be(1);

        var reads = proof.Database.Observation.Reads;
        using var account = await SendAsync(client, proof.Token(claimActor, SeedData.English, subject: ClaimSubject),
            "localhost", TenantAuthenticationHttpRequest.AccountPath);
        await ProblemAsync(account, HttpStatusCode.NotFound, "not_found");
        proof.Database.Observation.Reads.Should().Be(reads,
            "confirmed claim authority does not manufacture AccountSite's independent institution host");
    }

    [Fact]
    public async Task Coverage_capability_and_store_failure_are_distinct_and_never_dispatch_the_directory()
    {
        await using var proof = await CreateAsync(parent);
        var stranger = UserId.From(Guid.CreateVersion7());
        const string StrangerSubject = "directory-bound-no-membership";
        await proof.Database.AdoptAsync(stranger, StrangerSubject);
        using var client = proof.Client();
        using (var positive = await SendAsync(client, proof.Token(EnglishAdmin, SeedData.English)))
            await PageAsync(positive);
        var reads = proof.Observation.DirectoryReads;
        using var forbidden = await SendAsync(client, proof.Token(EnglishLearner, SeedData.English));
        await ProblemAsync(forbidden, HttpStatusCode.Forbidden, "forbidden");
        (await proof.AuditAsync(forbidden, SeedData.English)).Should().ContainSingle()
            .Which.Operation.Should().Be("identity.authorization.deny");
        using var uncovered = await SendAsync(client, proof.Token(stranger, SeedData.English, subject: StrangerSubject));
        await ProblemAsync(uncovered, HttpStatusCode.NotFound, "not_found");
        (await proof.AuditAsync(uncovered, SeedData.English)).Should().ContainSingle()
            .Which.Operation.Should().Be("identity.authorization.deny");
        await proof.OwnerAsync("REVOKE SELECT ON public.memberships FROM learnstack_app");
        try
        {
            using var unavailable = await SendAsync(client, proof.Token(EnglishAdmin, SeedData.English));
            await ProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
            (await unavailable.Content.ReadAsStringAsync()).Should().NotContain("items");
        }
        finally { await proof.OwnerAsync("GRANT SELECT ON public.memberships TO learnstack_app"); }
        proof.Observation.DirectoryReads.Should().Be(reads);
        using var recovered = await SendAsync(client, proof.Token(EnglishAdmin, SeedData.English));
        await PageAsync(recovered);
    }

    [Fact]
    public async Task Matching_host_revocation_is_observed_by_directory_and_AccountSite_after_committed_membership_change()
    {
        await using var proof = await CreateAsync(parent);
        var actor = UserId.From(Guid.CreateVersion7());
        const string Subject = "matching-host-revocation-subject";
        await proof.Database.AdoptAsync(actor, Subject);
        var membership = await proof.Database.AddMembershipAsync(SeedData.Yoga, actor, role: BuiltInRoleKey.TenantAdmin);
        using var client = proof.Client();
        var token = proof.Token(actor, SeedData.Yoga, organization: true, subject: Subject);
        using (var directory = await SendAsync(client, token, SeedData.Yoga.Host)) await PageAsync(directory);
        using (var account = await SendAsync(client, token, SeedData.Yoga.Host, TenantAuthenticationHttpRequest.AccountPath))
            account.StatusCode.Should().Be(HttpStatusCode.OK);
        var directoryReads = proof.Observation.DirectoryReads;
        var accountReads = proof.Database.Observation.Reads;
        await proof.SetMembershipStatusAsync(SeedData.Yoga, actor, membership, "Revoked");
        foreach (var path in new[] { DirectoryPath, TenantAuthenticationHttpRequest.AccountPath })
        {
            using var refused = await SendAsync(client, token, SeedData.Yoga.Host, path);
            await ProblemAsync(refused, HttpStatusCode.NotFound, "not_found");
            (await proof.AuditAsync(refused, SeedData.Yoga, SeedData.Yoga.DefaultOrganization.OrganizationId))
                .Should().ContainSingle().Which.Operation.Should().Be("identity.authorization.deny");
        }
        proof.Observation.DirectoryReads.Should().Be(directoryReads);
        proof.Database.Observation.Reads.Should().Be(accountReads);
    }

    [Fact]
    public async Task Anonymous_public_read_reaches_application_SQL_without_identity_authorization_queries()
    {
        await using var proof = await CreateAsync(parent);
        using var client = proof.Client();
        using var response = await TenantAuthenticationHttpRequest.Send(client, SeedData.English.Host,
            TenantAuthenticationHttpRequest.PublicPath);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        proof.Database.Observation.Reads.Should().Be(1);
        proof.Observation.AuthorizationReads.Should().Be(0);
        proof.Observation.BindingReads.Should().Be(0);
        proof.Issuer.MetadataRequests.Should().Be(0);
        proof.Observation.DirectoryReads.Should().Be(0);
        using var control = await SendAsync(client, proof.Token(EnglishAdmin, SeedData.English));
        await PageAsync(control);
        proof.Observation.BindingReads.Should().Be(1);
        proof.Observation.AuthorizationReads.Should().BeGreaterThanOrEqualTo(2,
            "the observer must see the real authority reads on its positive control");
    }

    [Fact]
    public async Task Directory_wrong_methods_retain_routing_semantics_without_handler_dispatch()
    {
        await using var proof = await CreateAsync(parent);
        using var client = proof.Client();
        var token = proof.Token(EnglishAdmin, SeedData.English);
        foreach (var method in new[] { "HEAD", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" })
        {
            using var response = await SendAsync(client, token, method: method);
            response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
            response.Content.Headers.Allow.Should().BeEquivalentTo(["GET"]);
            TenantAuthenticationHttpTests.NoStore(response);
        }
        proof.Observation.DirectoryReads.Should().Be(0);
        using var unmatched = await SendAsync(client, token, path: "/api/v1/identity/not-a-route");
        unmatched.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
