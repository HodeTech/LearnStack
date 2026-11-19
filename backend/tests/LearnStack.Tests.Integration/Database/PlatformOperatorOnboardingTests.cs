using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using LearnStack.SharedKernel.Platform;
using Microsoft.AspNetCore.Hosting;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The five operator onboarding acts through the actual Program, the operator bearer scheme and the
/// ordinary pipeline, against PostgreSQL as the application role (ADR-0102).
/// </summary>
/// <remarks>
/// <para>
/// Every state assertion reads as <c>learnstack_app</c> under the tenant's own announcement, so what is
/// observed is what row security shows that tenant. The only owner-credential reads are of
/// <c>platform_host_to_tenant</c>, which has no tenant to announce, and the fixture's one deliberate
/// corruption (an administrator revoked outside the last-admin guard so the recovery has something to
/// recover).
/// </para>
/// <para>
/// An act is a <c>PUT</c> on operator-supplied identifiers, so each case that proves an act also
/// sends it again: a reply lost on the wire must cost nothing and write nothing twice.
/// </para>
/// </remarks>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class PlatformOperatorOnboardingTests(PublicReadFixture parent)
{
    private const string Full = "operator-full";
    private const string TenantOnly = "operator-tenant-only";
    private const string BootstrapOnly = "operator-bootstrap-only";

    [Fact]
    public async Task An_Operator_Onboards_A_Tenant_Step_By_Step_And_Every_Act_Is_Audited_And_Repeatable()
    {
        await using var proof = await Proof.CreateAsync(parent);
        using var outcomes = new ActOutcomes();
        var tenant = Ids.Next();
        var organization = Ids.Next();
        var role = Ids.Next();
        var membership = Ids.Next();
        var slug = "onboard-" + Ids.Short();
        var host = slug + ".example.test";

        // Provision, and send the same act again.
        var provision = await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization));
        await AssertOkAsync(provision);
        using (var answered = JsonDocument.Parse(await provision.Content.ReadAsStringAsync()))
        {
            var correlation = answered.RootElement.GetProperty("correlationId").GetString();
            (await proof.CorrelationIdsAsync(tenant, "tenancy.tenant.create")).Should().Contain(correlation,
                "the runbook finds an act's rows by the correlation id its answer returned");
        }

        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization)));
        (await proof.CountAsync(tenant, "tenants")).Should().Be(1);
        (await proof.CountAsync(tenant, "organizations")).Should().Be(1);

        // An exact repeat answers from what was stored; a repeat that changes any value is a different request.
        using (var repeat = await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization)))
        {
            using var body = JsonDocument.Parse(await repeat.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("result").GetProperty("defaultOrganizationSlug").GetString().Should().Be("main");
        }

        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization, displayName: "Renamed")),
            HttpStatusCode.Conflict, "lockey_identifier_taken");
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization, organizationSlug: "elsewhere")),
            HttpStatusCode.Conflict, "lockey_identifier_taken");

        // The first administrator needs the role and the person first; each refusal writes nothing.
        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(role, proof.PersonSubject)),
            HttpStatusCode.Conflict, "lockey_role_not_initialized");
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{role}", RoleBody()));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{role}", RoleBody()));
        (await proof.CountAsync(tenant, "roles")).Should().Be(1);
        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(role, "nobody-has-signed-in")),
            HttpStatusCode.Conflict, "lockey_bootstrap_required");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(0);

        await AssertOkAsync(await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(role, proof.PersonSubject)));
        await AssertOkAsync(await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(role, proof.PersonSubject)));
        (await proof.CountAsync(tenant, "memberships")).Should().Be(1);
        (await proof.CountAsync(tenant, "membership_role_assignments")).Should().Be(1);

        // The host mapping, then again.
        var mapped = await proof.PutAsync(Full, $"/tenants/{tenant}/host-mappings/{host}", """{"isActive":true,"isPubliclyLive":true}""");
        await AssertOkAsync(mapped);
        proof.Invalidations.Should().Equal([host], "the first run drops any cached answer for the host");
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/host-mappings/{host}", """{"isActive":true,"isPubliclyLive":true}"""));
        proof.Invalidations.Should().Equal([host, host], "the retry drops it again, which is the repair for a miss cached before the commit");
        (await proof.HostMappingsAsync(host)).Should().Equal(tenant);

        // How each act ended, as the monitoring owner sees it: four first runs, five repeats that found their own
        // earlier result (the provisioning twice and each of the others once) and four refusals.
        outcomes.Counts.Should().Equal(new Dictionary<string, int>
        {
            ["applied"] = 4,
            ["replayed"] = 5,
            ["refused"] = 4,
        });

        // Who the business columns name, and what the audit rows say.
        (await proof.ScalarAsync<Guid>(tenant, "SELECT created_by FROM public.memberships")).Should().Be(UserId.SystemActor.Value);
        (await proof.ScalarAsync<Guid>(tenant, "SELECT user_id FROM public.memberships")).Should().Be(proof.Person.Value);
        var audit = await proof.AuditAsync(tenant);
        audit.Where(row => row.Outcome == "success").Select(row => row.Operation).Distinct().Should().BeEquivalentTo(
            ["tenancy.tenant.create", "tenancy.organization.create", "identity.role.initialize",
             "identity.membership.create_first_admin", "tenancy.hostmapping.write"]);
        audit.Should().OnlyContain(row => row.ActorUserId == null, "an operator act names no tenant user; the operator is in metadata");
        audit.Where(row => row.Outcome == "failed").Select(row => row.Operation).Should().BeEquivalentTo(
            ["identity.membership.create_first_admin", "identity.membership.create_first_admin",
             "tenancy.tenant.create", "tenancy.organization.create", "tenancy.tenant.create", "tenancy.organization.create"],
            "the two refused first-administrator attempts and the two changed provisioning repeats are recorded as the "
            + "failures they were, and nothing else failed");
        audit.Should().OnlyContain(row => row.Metadata.Contains(proof.Operator.Issuer, StringComparison.Ordinal),
            "every row carries the admitted operator's issuer");
        audit.Should().OnlyContain(row => !row.Metadata.Contains(proof.PersonSubject, StringComparison.Ordinal)
            && !row.Metadata.Contains(Full, StringComparison.Ordinal), "neither the person's subject nor the operator's raw subject is stored");
        audit.Should().OnlyContain(row => !row.Metadata.Contains("INC-77", StringComparison.Ordinal), "the ticket stays out of rows a tenant can read");
        audit.Select(row => row.Reason).Distinct().Should().BeEquivalentTo(
            ["tenant_provision", "role_initialize", "first_admin_create", "host_map"]);
        foreach (var row in audit)
        {
            using var metadata = JsonDocument.Parse(row.Metadata);
            var @operator = metadata.RootElement.GetProperty("operator");
            @operator.GetProperty("issuer").GetString().Should().Be(proof.Operator.Issuer);
            @operator.GetProperty("acr").GetString().Should().Be("mfa");
            @operator.GetProperty("kind").GetString().Should().Be("human");
            @operator.GetProperty("executor").GetString().Should().NotBeNullOrWhiteSpace();
            @operator.GetProperty("subjectDigest").GetString().Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
            metadata.RootElement.GetProperty("authorization").GetProperty("source").GetString().Should().Be("operator");
        }
    }

    [Fact]
    public async Task A_Grant_Is_Evaluated_Per_Act_And_A_Refused_Act_Writes_Nothing()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var organization = Ids.Next();
        var slug = "grants-" + Ids.Short();

        // Holding the tenant grant is not holding the bootstrap grant, and the reverse.
        (await proof.PutAsync(BootstrapOnly, $"/tenants/{tenant}", ProvisionBody(slug, organization))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        await AssertOkAsync(await proof.PutAsync(TenantOnly, $"/tenants/{tenant}", ProvisionBody(slug, organization)));

        var role = Ids.Next();
        (await proof.PutAsync(TenantOnly, $"/tenants/{tenant}/roles/{role}", RoleBody())).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await proof.PutAsync(TenantOnly, FirstAdminPath(tenant, Ids.Next()), AdminBody(role, proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await proof.PutAsync(TenantOnly, RecoveryPath(tenant, Ids.Next()), AdminBody(role, proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);
        (await proof.PutAsync("somebody-without-a-grant", $"/tenants/{Ids.Next()}", ProvisionBody("x-" + Ids.Short(), Ids.Next()))).StatusCode
            .Should().Be(HttpStatusCode.Forbidden);

        (await proof.CountAsync(tenant, "roles")).Should().Be(0, "a refused act reaches no handler");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(0);
    }

    [Fact]
    public async Task An_Act_On_A_Tenant_That_Does_Not_Exist_Is_Not_Found_And_A_Taken_Name_Is_Refused()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var missing = Ids.Next();

        (await proof.PutAsync(Full, $"/tenants/{missing}/host-mappings/missing.example.test", LiveFlags)).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await proof.PutAsync(Full, $"/tenants/{missing}/roles/{Ids.Next()}", RoleBody())).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
        (await proof.PutAsync(Full, FirstAdminPath(missing, Ids.Next()), AdminBody(Ids.Next(), proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.NotFound);

        var first = Ids.Next();
        var slug = "taken-" + Ids.Short();
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{first}", ProvisionBody(slug, Ids.Next())));
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{Ids.Next()}", ProvisionBody(slug, Ids.Next())),
            HttpStatusCode.Conflict, "lockey_slug_taken");
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{first}", ProvisionBody("another-" + Ids.Short(), Ids.Next())),
            HttpStatusCode.Conflict, "lockey_identifier_taken");
    }

    [Fact]
    public async Task Identifiers_That_Are_Not_UuidV7_Are_Refused_Before_Any_Write()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var v4 = Guid.NewGuid();
        var tenant = Ids.Next();

        (await proof.PutAsync(Full, $"/tenants/{v4}", ProvisionBody("v4-" + Ids.Short(), Ids.Next()))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "the tenant id is the act's identity and must be a UUIDv7");
        (await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody("v4-" + Ids.Short(), v4))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "nor may the default organization's");
        (await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{v4}", RoleBody())).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await proof.PutAsync(Full, FirstAdminPath(tenant, v4), AdminBody(Ids.Next(), proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(v4, proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await proof.PutAsync(Full, RecoveryPath(tenant, v4), AdminBody(Ids.Next(), proof.PersonSubject))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await proof.CountAsync(v4, "tenants")).Should().Be(0);
        (await proof.CountAsync(tenant, "tenants")).Should().Be(0);
    }

    [Fact]
    public async Task A_Person_Whose_Binding_Is_No_Longer_Active_Cannot_Be_Named_An_Administrator()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var role = Ids.Next();
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody("closing-" + Ids.Short(), Ids.Next())));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{role}", RoleBody()));
        await proof.CloseUserAsync(proof.Person);

        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(role, proof.PersonSubject)),
            HttpStatusCode.Conflict, "lockey_bootstrap_required");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(0);
    }

    [Fact]
    public async Task A_Reply_Lost_After_The_Commit_Is_Answered_By_Sending_The_Same_Request_Again()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var organization = Ids.Next();
        var slug = "lost-" + Ids.Short();
        using var lossy = proof.ClientWith(services => services.Insert(0, ServiceDescriptor.Scoped<
            IPipelineBehavior<OperatorProvisionTenantCommand, Result<ProvisionedTenantDto>>, LoseTheReplyAfterTheCommit>()));

        using var lost = await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization), lossy);

        lost.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the act committed and the operator was told it failed");
        (await proof.CountAsync(tenant, "tenants")).Should().Be(1, "the commit is real whatever the operator was told");
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization)));
        (await proof.CountAsync(tenant, "tenants")).Should().Be(1);
        (await proof.CountAsync(tenant, "organizations")).Should().Be(1);
        (await proof.AuditAsync(tenant)).Count(row => row.Operation == "tenancy.tenant.create" && row.Outcome == "success")
            .Should().Be(2, "the first run wrote the create; the retry is recorded as the repeated act it is, without a second tenant");
    }

    [Fact]
    public async Task An_Act_Interrupted_Between_Its_Writes_Leaves_Nothing_And_The_Retry_Completes_Once()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var organization = Ids.Next();
        var slug = "cut-" + Ids.Short();
        using var failing = proof.ClientWith(services =>
        {
            var registered = services.Last(descriptor => descriptor.ServiceType == typeof(ITenantWriteStore));
            services.Remove(registered);
            services.AddScoped<ITenantWriteStore>(provider => new FailTheLastWrite((ITenantWriteStore)(
                registered.ImplementationFactory?.Invoke(provider)
                ?? ActivatorUtilities.CreateInstance(provider, registered.ImplementationType!))));
        });

        using var cut = await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization), failing);

        cut.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await proof.CountAsync(tenant, "tenants")).Should().Be(0, "the tenant and organization inserts rolled back with the act");
        (await proof.CountAsync(tenant, "organizations")).Should().Be(0);
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody(slug, organization)));
        (await proof.CountAsync(tenant, "tenants")).Should().Be(1);
        (await proof.CountAsync(tenant, "organizations")).Should().Be(1);
    }

    [Fact]
    public async Task The_Platform_Sentinel_And_A_Malformed_Request_Are_Refused_With_The_Status_An_Operator_Can_Act_On()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var sentinel = TenantId.PlatformSentinel.Value;
        var tenant = Ids.Next();
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody("shape-" + Ids.Short(), Ids.Next())));

        (await proof.PutAsync(Full, $"/tenants/{sentinel}/roles/{Ids.Next()}", RoleBody())).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "the sentinel is the platform's own tenant and no act may name it");
        (await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(Ids.Next(), "   "))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "a blank subject is a malformed request");
        (await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(Ids.Next(), new string('x', 256)))).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "a subject over 255 characters is a malformed request");
        (await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{Ids.Next()}",
            """{"builtInKey":"TenantAdmin","capabilityScope":"Tenant","ticket":"has a space"}""")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "the ticket grammar is enforced at the gate");
        (await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{Ids.Next()}", "{ not json")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{Ids.Next()}", """{"builtInKey":"Nobody","capabilityScope":"Tenant"}""")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest, "an unknown template key is refused before any act");
        (await proof.SendAsync(Full, $"/tenants/{tenant}/roles/{Ids.Next()}", new StringContent(RoleBody(), Encoding.UTF8, "text/plain"))).StatusCode
            .Should().Be(HttpStatusCode.UnsupportedMediaType);
        (await proof.CountAsync(tenant, "roles")).Should().Be(0);
    }

    [Fact]
    public async Task A_Host_Mapping_States_Both_Flags_And_Refuses_A_Host_Another_Tenant_Holds_And_A_Foreign_Organization()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var first = Ids.Next();
        var second = Ids.Next();
        var secondOrganization = Ids.Next();
        var host = "shared-" + Ids.Short() + ".example.test";
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{first}", ProvisionBody("one-" + Ids.Short(), Ids.Next())));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{second}", ProvisionBody("two-" + Ids.Short(), secondOrganization)));

        // No unmap act exists, so a mapping never arrives serving traffic because a field was left out.
        (await proof.PutAsync(Full, $"/tenants/{first}/host-mappings/{host}", "{}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await proof.PutAsync(Full, $"/tenants/{first}/host-mappings/{host}", """{"isActive":true}""")).StatusCode
            .Should().Be(HttpStatusCode.BadRequest);
        (await proof.HostMappingsAsync(host)).Should().BeEmpty();

        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{first}/host-mappings/{host}", LiveFlags));
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{second}/host-mappings/{host}", LiveFlags),
            HttpStatusCode.Conflict, "lockey_host_taken");
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{first}/host-mappings/{host}", """{"isActive":true,"isPubliclyLive":false}"""),
            HttpStatusCode.Conflict, "lockey_host_taken");
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{first}/host-mappings/other-{Ids.Short()}.example.test",
                JsonSerializer.Serialize(new { organizationId = secondOrganization, isActive = true, isPubliclyLive = true })),
            HttpStatusCode.Conflict, "lockey_organization_not_in_tenant");
        (await proof.HostMappingsAsync(host)).Should().Equal(first);
    }

    [Fact]
    public async Task Organization_Scope_Templates_Are_Initialized_And_Only_A_Tenant_Administrator_Role_Of_This_Tenant_Can_Administer()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var other = Ids.Next();
        var adminRole = Ids.Next();
        var orgAdminRole = Ids.Next();
        var otherRole = Ids.Next();
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody("scoped-" + Ids.Short(), Ids.Next())));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{other}", ProvisionBody("other-" + Ids.Short(), Ids.Next())));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{adminRole}", RoleBody()));
        await AssertOkAsync(await proof.PutAsync(
            Full, $"/tenants/{tenant}/roles/{orgAdminRole}", """{"builtInKey":"OrgAdmin","capabilityScope":"Organization"}"""));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{other}/roles/{otherRole}", RoleBody()));
        (await proof.CountAsync(tenant, "roles")).Should().Be(2);

        // An Organization-scope administrator template and another tenant's administrator template are not this
        // tenant's tenant-wide administrator Role.
        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(orgAdminRole, proof.PersonSubject)),
            HttpStatusCode.Conflict, "lockey_role_not_initialized");
        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(otherRole, proof.PersonSubject)),
            HttpStatusCode.Conflict, "lockey_role_not_initialized");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(0);
        await AssertOkAsync(await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(adminRole, proof.PersonSubject)));
    }

    [Fact]
    public async Task A_Retry_Of_The_First_Administrator_Still_Answers_After_The_Role_Template_Was_Edited()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var (tenant, role, membership) = await proof.OnboardAsync(Full);
        await proof.EditRoleOutsideTheActsAsync(tenant, role);

        // The administrator edited the template after the first run, so it is no longer the pristine one the
        // act created; the retry of the original act must still find its Membership and answer.
        await AssertOkAsync(await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(role, proof.PersonSubject)));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{role}", RoleBody()));
        (await proof.CountAsync(tenant, "memberships")).Should().Be(1);
    }

    [Fact]
    public async Task The_First_Administrator_Is_Refused_Once_A_Tenant_Has_Had_Any_Member()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var (tenant, role, _) = await proof.OnboardAsync(Full);
        var (_, second) = await proof.AnotherPersonAsync();

        await AssertRuleAsync(
            await proof.PutAsync(Full, FirstAdminPath(tenant, Ids.Next()), AdminBody(role, second)),
            HttpStatusCode.Conflict, "lockey_tenant_has_memberships");
        // Nor may a Role template be created once a Membership exists.
        await AssertRuleAsync(
            await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{Ids.Next()}", RoleBody()),
            HttpStatusCode.Conflict, "lockey_tenant_has_memberships");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(1);
    }

    [Fact]
    public async Task A_Recovery_Is_Refused_While_An_Administrator_Is_Active_And_Succeeds_Once_None_Is()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var (tenant, role, firstMembership) = await proof.OnboardAsync(Full);
        var (secondUser, second) = await proof.AnotherPersonAsync();
        var replacement = Ids.Next();

        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, replacement), AdminBody(role, second)),
            HttpStatusCode.Conflict, "lockey_administrator_exists");
        (await proof.CountAsync(tenant, "memberships")).Should().Be(1);

        // The database never lets the last administrator be revoked, so the state a recovery exists for is
        // reached only by an owner-level repair that steps outside the guard; the fixture does exactly that.
        await proof.SuspendOutsideTheGuardAsync(tenant, firstMembership);

        await AssertOkAsync(await proof.PutAsync(Full, RecoveryPath(tenant, replacement), AdminBody(role, second)));
        await AssertOkAsync(await proof.PutAsync(Full, RecoveryPath(tenant, replacement), AdminBody(role, second)));
        (await proof.ScalarAsync<long>(tenant, "SELECT count(*) FROM public.memberships WHERE status='Active'")).Should().Be(1);
        (await proof.AuditAsync(tenant)).Should().Contain(row =>
            row.Operation == "identity.membership.recover_admin" && row.Reason == "admin_recover" && row.Outcome == "success");

        // The act is repeatable but not additive: with an administrator active again a third person is refused.
        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, Ids.Next()), AdminBody(role, (await proof.AnotherPersonAsync()).Subject)),
            HttpStatusCode.Conflict, "lockey_administrator_exists");

        // The last-administrator invariant is the database's and the recovery leaves it as it found it: the
        // replacement, now the only administrator, cannot be revoked by the application role.
        var revoke = () => proof.RevokeAsApplicationRoleAsync(tenant, replacement, secondUser);
        (await revoke.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be("P0303");
        (await proof.ScalarAsync<long>(tenant, "SELECT count(*) FROM public.memberships WHERE status='Active'")).Should().Be(1);
    }

    [Fact]
    public async Task A_Recovery_Names_An_Initialized_Administrator_Role_Of_The_Tenant_And_A_Person_Who_Has_Signed_In()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var (tenant, _, firstMembership) = await proof.OnboardAsync(Full);
        await proof.SuspendOutsideTheGuardAsync(tenant, firstMembership);
        var (_, second) = await proof.AnotherPersonAsync();

        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, Ids.Next()), AdminBody(Ids.Next(), second)),
            HttpStatusCode.Conflict, "lockey_role_not_initialized");
        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, Ids.Next()), AdminBody(Ids.Next(), "nobody-has-signed-in")),
            HttpStatusCode.Conflict, "lockey_bootstrap_required");
        (await proof.ScalarAsync<long>(tenant, "SELECT count(*) FROM public.memberships WHERE status='Active'")).Should().Be(0);
    }

    [Fact]
    public async Task A_Recovery_Is_Refused_For_A_Role_That_Is_Not_An_Administrator_Role_Of_The_Tenant()
    {
        await using var proof = await Proof.CreateAsync(parent);
        var tenant = Ids.Next();
        var adminRole = Ids.Next();
        var learnerRole = Ids.Next();
        var membership = Ids.Next();
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}", ProvisionBody("roles-" + Ids.Short(), Ids.Next())));
        await AssertOkAsync(await proof.PutAsync(Full, $"/tenants/{tenant}/roles/{adminRole}", RoleBody()));
        await AssertOkAsync(await proof.PutAsync(
            Full, $"/tenants/{tenant}/roles/{learnerRole}", """{"builtInKey":"Learner","capabilityScope":"Tenant"}"""));
        await AssertOkAsync(await proof.PutAsync(Full, FirstAdminPath(tenant, membership), AdminBody(adminRole, proof.PersonSubject)));
        await proof.SuspendOutsideTheGuardAsync(tenant, membership);
        var (_, second) = await proof.AnotherPersonAsync();

        await AssertRuleAsync(
            await proof.PutAsync(Full, RecoveryPath(tenant, Ids.Next()), AdminBody(learnerRole, second)),
            HttpStatusCode.Conflict, "lockey_role_not_initialized");
        (await proof.ScalarAsync<long>(tenant, "SELECT count(*) FROM public.memberships WHERE status='Active'")).Should().Be(0);
    }

    private const string LiveFlags = """{"isActive":true,"isPubliclyLive":true}""";

    private static string ProvisionBody(string slug, Guid organization, string? displayName = null, string organizationSlug = "main") =>
        JsonSerializer.Serialize(new
        {
            slug,
            displayName = displayName ?? "Onboarded " + slug,
            defaultOrganization = new { id = organization, slug = organizationSlug, displayName = "Main" },
            ticket = "INC-77",
        });

    private static string RoleBody() => """{"builtInKey":"TenantAdmin","capabilityScope":"Tenant","ticket":"INC-77"}""";

    private static string AdminBody(Guid role, string subject) => JsonSerializer.Serialize(new { roleId = role, providerSubject = subject, ticket = "INC-77" });

    private static string FirstAdminPath(Guid tenant, Guid membership) => $"/tenants/{tenant}/first-administrator/{membership}";

    private static string RecoveryPath(Guid tenant, Guid membership) => $"/tenants/{tenant}/administrator-recovery/{membership}";

    private static async Task AssertOkAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using var document = JsonDocument.Parse(text);
        document.RootElement.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    private static async Task AssertRuleAsync(HttpResponseMessage response, HttpStatusCode status, string reason)
    {
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(status, text);
        text.Should().Contain(reason);
    }

    private static class Ids
    {
        internal static Guid Next() => Guid.CreateVersion7();

        internal static string Short() => Guid.NewGuid().ToString("N")[..10];
    }

    private sealed record AuditRow(string Operation, string Outcome, Guid? ActorUserId, string? Reason, string Metadata);

    private sealed class Proof : IAsyncDisposable
    {
        private readonly HttpClient _client;
        private readonly Dictionary<string, string> _tokens = [];
        private readonly List<HttpClient> _extraClients = [];

        private Proof(TenantAuthenticationIssuer tenantIssuer, TenantAuthenticationIssuer @operator,
            IdentityPublicReadFixture database, UserId person, string personSubject, HttpClient client,
            List<string> invalidations)
        {
            Invalidations = invalidations;
            TenantIssuer = tenantIssuer;
            Operator = @operator;
            Database = database;
            Person = person;
            PersonSubject = personSubject;
            _client = client;
        }

        internal TenantAuthenticationIssuer TenantIssuer { get; }

        internal TenantAuthenticationIssuer Operator { get; }

        internal IdentityPublicReadFixture Database { get; }

        internal UserId Person { get; }

        internal string PersonSubject { get; }

        /// <summary>The hosts whose cached resolution the acts dropped, in order.</summary>
        internal List<string> Invalidations { get; }

        internal static async Task<Proof> CreateAsync(PublicReadFixture parent)
        {
            var tenantIssuer = await TenantAuthenticationIssuer.StartAsync();
            var @operator = await TenantAuthenticationIssuer.StartAsync();
            var person = UserId.From(Guid.CreateVersion7());
            var subject = "onboarding-person-" + person.Value.ToString("N");
            IdentityPublicReadFixture? database = null;
            HttpClient? client = null;
            var invalidations = new List<string>();
            try
            {
                database = await IdentityPublicReadFixture.CreateAsync(
                    parent, tenantIssuer.Issuer, new Dictionary<UserId, string> { [person] = subject });
                client = database.ClientFor("localhost",
                    services => services.AddSingleton<IHostResolutionInvalidator>(new RecordingInvalidator(invalidations)),
                    ConfigureOperator(@operator));
                return new Proof(tenantIssuer, @operator, database, person, subject, client, invalidations);
            }
            catch
            {
                client?.Dispose();
                if (database is not null) await database.DisposeAsync();
                await @operator.DisposeAsync();
                await tenantIssuer.DisposeAsync();
                throw;
            }
        }

        private static Action<IWebHostBuilder> ConfigureOperator(TenantAuthenticationIssuer @operator) => builder =>
        {
            builder.UseSetting("PlatformOperator:Issuer", @operator.Issuer);
            builder.UseSetting("PlatformOperator:AcceptedAcr:0", "mfa");
            builder.UseSetting("PlatformOperator:AuthorizedParties:0", "learnstack-operator");
            var grants = new (string Subject, string[] Keys)[]
            {
                (Full, [PlatformPermissions.TenantWrite.Key, PlatformPermissions.TenantBootstrapWrite.Key]),
                (TenantOnly, [PlatformPermissions.TenantWrite.Key]),
                (BootstrapOnly, [PlatformPermissions.TenantBootstrapWrite.Key]),
            };
            for (var index = 0; index < grants.Length; index++)
            {
                builder.UseSetting($"PlatformOperator:Grants:{index}:Subject", grants[index].Subject);
                for (var key = 0; key < grants[index].Keys.Length; key++)
                {
                    builder.UseSetting($"PlatformOperator:Grants:{index}:Permissions:{key}", grants[index].Keys[key]);
                }
            }
        };

        /// <summary>
        /// A second host over the same database whose services <paramref name="configure"/> changes, for the cases
        /// that make an act fail where the production code would not.
        /// </summary>
        internal HttpClient ClientWith(Action<IServiceCollection> configure)
        {
            var client = Database.ClientFor("localhost", configure, ConfigureOperator(Operator));
            _extraClients.Add(client);
            return client;
        }

        internal async Task<HttpResponseMessage> PutAsync(string operatorSubject, string path, string json, HttpClient? client = null) =>
            await SendAsync(operatorSubject, path, new StringContent(json, Encoding.UTF8, "application/json"), client);

        internal async Task<HttpResponseMessage> SendAsync(
            string operatorSubject, string path, HttpContent content, HttpClient? client = null)
        {
            if (!_tokens.TryGetValue(operatorSubject, out var token))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                token = Operator.Token(JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["iss"] = Operator.Issuer,
                    ["sub"] = operatorSubject,
                    ["aud"] = "learnstack-platform",
                    ["iat"] = now - 5,
                    ["exp"] = now + 600,
                    ["azp"] = "learnstack-operator",
                    ["acr"] = "mfa",
                    ["auth_time"] = now - 30,
                }));
                _tokens[operatorSubject] = token;
            }

            using var request = new HttpRequestMessage(HttpMethod.Put, "/api/platform/v1" + path)
            {
                Headers = { Authorization = new AuthenticationHeaderValue("Bearer", token) },
                Content = content,
            };
            return await (client ?? _client).SendAsync(request);
        }

        /// <summary>The correlation ids of the audit rows of one operation in the tenant.</summary>
        internal async Task<List<string>> CorrelationIdsAsync(Guid tenant, string operation)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant);
            await using var command = new NpgsqlCommand(
                "SELECT correlation_id FROM public.audit_log WHERE operation=@operation", app, transaction);
            command.Parameters.AddWithValue("operation", operation);
            await using var reader = await command.ExecuteReaderAsync();
            var ids = new List<string>();
            while (await reader.ReadAsync())
            {
                ids.Add(reader.GetString(0));
            }

            return ids;
        }

        /// <summary>A person who has signed in, so the first-sign-in bootstrap has bound them.</summary>
        internal async Task<(UserId Person, string Subject)> AnotherPersonAsync()
        {
            var person = UserId.From(Guid.CreateVersion7());
            var subject = "onboarding-person-" + person.Value.ToString("N");
            await Database.AdoptAsync(person, subject);
            return (person, subject);
        }

        /// <summary>
        /// A user closed, with the immutability trigger and the insert check stepped around exactly as the
        /// administration-eligibility cases do, and restored before the next act runs.
        /// </summary>
        internal async Task CloseUserAsync(UserId user)
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.MigrationConnectionString);
            var check = await IdentityMembershipFixture.ScalarAsync<string>(owner, null,
                "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'");
            await using var transaction = await owner.BeginTransactionAsync();
            await IdentityMembershipFixture.ExecuteAsync(owner, transaction, $"""
                ALTER TABLE public.users DISABLE TRIGGER tg_users_no_update;
                ALTER POLICY users_isolation ON public.users WITH CHECK (({check}) OR
                    (current_user='learnstack_migration' AND id='{user.Value:D}'::uuid AND status='Closing'
                     AND deleted_at IS NULL AND deleted_by IS NULL));
                UPDATE public.users SET status='Closing' WHERE id='{user.Value:D}'::uuid;
                ALTER POLICY users_isolation ON public.users WITH CHECK ({check});
                ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update;
                """);
            await transaction.CommitAsync();
        }

        /// <summary>
        /// A later edit of the Role by its owner, which bumps the row version the way an administrator's edit does, with
        /// the same locks a handler would take.
        /// </summary>
        internal async Task EditRoleOutsideTheActsAsync(Guid tenant, Guid role)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant);
            await IdentityMembershipFixture.LockAsync(app, transaction, tenant, null, exclusive: true);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction,
                "UPDATE public.roles SET row_version=row_version+1, updated_at=clock_timestamp(), updated_by=@actor WHERE id=@role",
                ("actor", UserId.SystemActor.Value), ("role", role));
            await transaction.CommitAsync();
        }

        /// <summary>The whole onboarding through the real routes; returns the tenant, its role and its first membership.</summary>
        internal async Task<(Guid Tenant, Guid Role, Guid Membership)> OnboardAsync(string operatorSubject)
        {
            var tenant = Ids.Next();
            var role = Ids.Next();
            var membership = Ids.Next();
            await AssertOkAsync(await PutAsync(operatorSubject, $"/tenants/{tenant}", ProvisionBody("full-" + Ids.Short(), Ids.Next())));
            await AssertOkAsync(await PutAsync(operatorSubject, $"/tenants/{tenant}/roles/{role}", RoleBody()));
            await AssertOkAsync(await PutAsync(operatorSubject, FirstAdminPath(tenant, membership), AdminBody(role, PersonSubject)));
            return (tenant, role, membership);
        }

        internal Task<long> CountAsync(Guid tenant, string table) =>
            ScalarAsync<long>(tenant, $"SELECT count(*) FROM public.{table}");

        internal async Task<T> ScalarAsync<T>(Guid tenant, string sql)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant);
            await using var command = new NpgsqlCommand(sql, app, transaction);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        internal async Task<List<AuditRow>> AuditAsync(Guid tenant)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant);
            await using var command = new NpgsqlCommand(
                "SELECT operation,outcome,actor_user_id,reason,metadata::text FROM public.audit_log ORDER BY timestamp", app, transaction);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<AuditRow>();
            while (await reader.ReadAsync())
            {
                rows.Add(new(
                    reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetGuid(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4)));
            }

            return rows;
        }

        /// <summary>
        /// What the resolver itself would read for the host: the application role, announcing only the host
        /// it is resolving, which is the one thing row security lets it see of the host index.
        /// </summary>
        internal async Task<List<Guid>> HostMappingsAsync(string host)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ExecuteAsync(
                app, transaction, "SELECT set_config('app.resolving_host', @host, true)", ("host", host));
            await using var command = new NpgsqlCommand(
                "SELECT tenant_id FROM public.platform_host_to_tenant WHERE host=@host", app, transaction);
            command.Parameters.AddWithValue("host", host);
            await using var reader = await command.ExecuteReaderAsync();
            var tenants = new List<Guid>();
            while (await reader.ReadAsync())
            {
                tenants.Add(reader.GetGuid(0));
            }

            return tenants;
        }

        /// <summary>
        /// The fixture's one deliberate corruption: the only administrator suspended with the deferred
        /// last-administrator guard stepped around, which is the state a recovery exists for.
        /// </summary>
        /// <remarks>
        /// The owner is subject to forced row security, so it announces the tenant like any other connection and
        /// takes the same locks; what it skips is the commit-time guard that refuses a tenant with no administrator.
        /// </remarks>
        internal async Task SuspendOutsideTheGuardAsync(Guid tenant, Guid membership)
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.MigrationConnectionString);
            await using var transaction = await owner.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(owner, transaction, tenant);
            await IdentityMembershipFixture.LockAsync(owner, transaction, tenant, Person.Value, exclusive: true);
            await IdentityMembershipFixture.ExecuteAsync(owner, transaction, "ALTER TABLE public.memberships DISABLE TRIGGER tg_memberships_complete_graph");
            await IdentityMembershipFixture.ExecuteAsync(
                owner, transaction, "UPDATE public.memberships SET status='Suspended' WHERE id=@membership", ("membership", membership));
            await IdentityMembershipFixture.ExecuteAsync(owner, transaction, "ALTER TABLE public.memberships ENABLE TRIGGER tg_memberships_complete_graph");
            await transaction.CommitAsync();
        }

        /// <summary>An ordinary revocation by the application role, under the locks a handler would take.</summary>
        internal async Task RevokeAsApplicationRoleAsync(Guid tenant, Guid membership, UserId user)
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(Database.AppConnectionString);
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await using var transaction = await app.BeginTransactionAsync();
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant);
            await IdentityMembershipFixture.LockAsync(app, transaction, tenant, user.Value, exclusive: true);
            await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
                UPDATE public.memberships SET status='Revoked', deleted_at=clock_timestamp(), deleted_by=@actor WHERE id=@membership
                """, ("actor", UserId.SystemActor.Value), ("membership", membership));
            await transaction.CommitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var extra in _extraClients)
            {
                extra.Dispose();
            }

            _client.Dispose();
            await Database.DisposeAsync();
            await Operator.DisposeAsync();
            await TenantIssuer.DisposeAsync();
        }
    }

    /// <summary>Counts the outcome label of every onboarding act counted while it is alive.</summary>
    private sealed class ActOutcomes : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly Dictionary<string, int> _counts = [];

        public ActOutcomes()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == LearnStack.Infrastructure.PlatformOperator.PlatformOperatorMetrics.MeterName
                    && instrument.Name == LearnStack.Infrastructure.PlatformOperator.PlatformOperatorMetrics.ActCounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag is { Key: "outcome", Value: string outcome })
                    {
                        lock (_counts)
                        {
                            _counts[outcome] = _counts.GetValueOrDefault(outcome) + 1;
                        }
                    }
                }
            });
            _listener.Start();
        }

        public IReadOnlyDictionary<string, int> Counts
        {
            get
            {
                lock (_counts)
                {
                    return new Dictionary<string, int>(_counts);
                }
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private sealed class RecordingInvalidator(List<string> hosts) : IHostResolutionInvalidator
    {
        public Task InvalidateAsync(string normalizedHost, CancellationToken cancellationToken = default)
        {
            hosts.Add(normalizedHost);
            return Task.CompletedTask;
        }
    }

    /// <summary>A reply lost after the commit: the outermost behavior fails once the whole act, commit included, is done.</summary>
    private sealed class LoseTheReplyAfterTheCommit
        : IPipelineBehavior<OperatorProvisionTenantCommand, Result<ProvisionedTenantDto>>
    {
        public async Task<Result<ProvisionedTenantDto>> Handle(
            OperatorProvisionTenantCommand request,
            RequestHandlerDelegate<Result<ProvisionedTenantDto>> next,
            CancellationToken cancellationToken)
        {
            await next();
            throw new InvalidOperationException("The reply was lost after the commit.");
        }
    }

    /// <summary>The tenant store with its third write, the default-organization back-reference, failing.</summary>
    private sealed class FailTheLastWrite(ITenantWriteStore inner) : ITenantWriteStore
    {
        public Task AddAsync(LearnStack.Modules.Tenancy.Domain.Tenant aggregate, CancellationToken cancellationToken = default) =>
            inner.AddAsync(aggregate, cancellationToken);

        public Task UpdateAsync(LearnStack.Modules.Tenancy.Domain.Tenant aggregate, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The act was interrupted between its writes.");

        public Task<LearnStack.Modules.Tenancy.Domain.Tenant?> FindAsync(TenantId id, CancellationToken cancellationToken = default) =>
            inner.FindAsync(id, cancellationToken);

        public Task<LearnStack.Modules.Tenancy.Domain.Tenant?> FindForUpdateAsync(TenantId id, CancellationToken cancellationToken = default) =>
            inner.FindForUpdateAsync(id, cancellationToken);
    }
}

