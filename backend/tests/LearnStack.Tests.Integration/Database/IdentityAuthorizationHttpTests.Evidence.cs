using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityAuthorizationHttpTests
{
    [Fact]
    public async Task Confirmed_claim_header_rejections_write_exact_private_evidence_without_spending_the_host_budget()
    {
        await using var proof = await CreateAsync(parent);
        var budget = new TenantAssertionAuditBudget(new FixedBudgetClock());
        for (var index = 0; index < TenantAssertionAuditBudget.PermitPerWindow - 1; index++)
            budget.TryAcquire(SeedData.English.TenantId.Value).Should().BeTrue();
        using var client = proof.Client(services => services.Replace(ServiceDescriptor.Singleton(budget)));
        var token = proof.Token(EnglishAdmin, SeedData.English, organization: true);
        using (var control = await SendAsync(client, token, "localhost")) await PageAsync(control);
        var reads = proof.Observation.DirectoryReads;
        var asserted = Guid.CreateVersion7().ToString("D");
        foreach (var dimension in new[] { "tenant", "organization" })
        {
            using var response = await SendAsync(client, token, "localhost",
                tenantAssertion: dimension == "tenant" ? asserted : null,
                organizationAssertion: dimension == "organization" ? asserted : null);
            await ProblemAsync(response, HttpStatusCode.NotFound, "tenant_mismatch");
            var row = (await proof.AuditAsync(response, SeedData.English,
                SeedData.English.DefaultOrganization.OrganizationId)).Should().ContainSingle().Subject;
            row.Operation.Should().Be("tenancy.assertion.reject_claim_context");
            row.Outcome.Should().Be("denied");
            row.Actor.Should().Be(EnglishAdmin.Value);
            row.Tenant.Should().Be(SeedData.English.TenantId.Value);
            row.Metadata.Should().NotContain(asserted).And.Contain("ListMemberships");
            using var metadata = JsonDocument.Parse(row.Metadata);
            metadata.RootElement.GetProperty("assertion_source").GetString().Should().Be("membership_confirmed_header");
            if (dimension == "tenant")
            {
                budget.TryAcquire(SeedData.English.TenantId.Value).Should().BeTrue(
                    "the claim-only event must leave the final host allowance untouched");
                budget.TryAcquire(SeedData.English.TenantId.Value).Should().BeFalse();
            }
        }
        proof.Observation.DirectoryReads.Should().Be(reads,
            "headers refuse before directory dispatch, even with an exhausted host recorder budget");
    }

    [Fact]
    public async Task Resolver_noncoverage_preserves_row_7_host_evidence_row_10_silence_and_row_14_diagnostic_only()
    {
        await using var proof = await CreateAsync(parent);
        var actor = UserId.From(Guid.CreateVersion7());
        const string Subject = "resolution-no-membership-subject";
        await proof.Database.AdoptAsync(actor, Subject);
        using var client = proof.Client();
        foreach (var row in new[] { 7, 10, 14 })
        {
            var tenant = row == 10 ? SeedData.Yoga : SeedData.English;
            using var response = await SendAsync(client, proof.Token(actor, tenant, row != 10, Subject),
                row == 14 ? "localhost" : tenant.Host);
            await ProblemAsync(response, HttpStatusCode.NotFound, "not_found");
            var evidence = await proof.AuditAsync(response, tenant, tenant.DefaultOrganization.OrganizationId);
            if (row == 7)
                evidence.Should().ContainSingle().Which.Operation.Should().Be(AuditingTenantAssertionRecorder.RejectOperation);
            else evidence.Should().BeEmpty();
        }
        proof.Observation.DirectoryReads.Should().Be(0);
        proof.Observation.AuthorizationReads.Should().Be(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sensitive_HTTP_result_is_withheld_on_MUST_insert_or_deferred_COMMIT_failure(bool deferred)
    {
        await using var proof = await CreateAsync(parent);
        using var client = proof.Client();
        var token = proof.Token(EnglishAdmin, SeedData.English);
        using (var control = await SendAsync(client, token)) await PageAsync(control);
        await InstallAuditTriggerAsync(proof, "identity.membership.list", deferred, gate: false);
        try
        {
            using var response = await SendAsync(client, token);
            await ProblemAsync(response, HttpStatusCode.ServiceUnavailable, "audit_unavailable");
            (await response.Content.ReadAsStringAsync()).Should().NotContain("items").And.NotContain("Demo Admin");
            (await proof.AuditAsync(response, SeedData.English)).Should().BeEmpty();
            proof.Observation.DirectoryReads.Should().Be(2, "the real result was materialized before the durable barrier refused it");
        }
        finally { await RemoveAuditTriggerAsync(proof); }
        using var recovered = await SendAsync(client, token);
        await PageAsync(recovered);
        (await proof.AuditAsync(recovered, SeedData.English)).Should().ContainSingle();
    }

    [Fact]
    public async Task HTTP_success_waits_for_physical_sensitive_audit_COMMIT()
    {
        await using var proof = await CreateAsync(parent);
        using var client = proof.Client();
        await InstallAuditTriggerAsync(proof, "identity.membership.list", deferred: true, gate: true);
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(blocker);
        await using var transaction = await blocker.BeginTransactionAsync();
        await ExecuteAsync(blocker, transaction, "SELECT pg_advisory_xact_lock(@key)", ("key", HttpCommitGate));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request = SendAsync(client, proof.Token(EnglishAdmin, SeedData.English));
        try
        {
            await using var monitor = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
            while (!await ScalarAsync<bool>(monitor, null, """
                SELECT EXISTS(SELECT 1 FROM pg_stat_activity
                    WHERE @blocker=ANY(pg_blocking_pids(pid)) AND query='COMMIT' AND state='active')
                """, ("blocker", blocker.ProcessID)))
            {
                request.IsCompleted.Should().BeFalse("no HTTP result is released before the physical COMMIT barrier");
                await Task.Delay(20, deadline.Token);
            }
            request.IsCompleted.Should().BeFalse();
            await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString);
            await using var observation = await observer.BeginTransactionAsync();
            await ScopeAsync(observer, observation, SeedData.English.TenantId.Value);
            (await ScalarAsync<long>(observer, observation,
                "SELECT count(*) FROM audit_log WHERE operation='identity.membership.list'"))
                .Should().Be(0, "the pending MUST row is not yet committed");
        }
        finally { await transaction.RollbackAsync(CancellationToken.None); }
        using var response = await request.WaitAsync(TimeSpan.FromSeconds(20));
        await PageAsync(response);
        (await proof.AuditAsync(response, SeedData.English)).Should().ContainSingle();
        await RemoveAuditTriggerAsync(proof);
    }

    [Fact]
    public async Task An_audit_outage_preserves_actual_HTTP_capability_and_coverage_refusals()
    {
        await using var proof = await CreateAsync(parent);
        var stranger = UserId.From(Guid.CreateVersion7());
        const string Subject = "denial-audit-outage-subject";
        await proof.Database.AdoptAsync(stranger, Subject);
        using var client = proof.Client();
        using (var control = await SendAsync(client, proof.Token(EnglishLearner, SeedData.English)))
        {
            await ProblemAsync(control, HttpStatusCode.Forbidden, "forbidden");
            (await proof.AuditAsync(control, SeedData.English)).Should().ContainSingle();
        }
        await InstallAuditTriggerAsync(proof, "identity.authorization.deny", deferred: false, gate: false);
        try
        {
            using var forbidden = await SendAsync(client, proof.Token(EnglishLearner, SeedData.English));
            await ProblemAsync(forbidden, HttpStatusCode.Forbidden, "forbidden");
            using var uncovered = await SendAsync(client, proof.Token(stranger, SeedData.English, subject: Subject));
            await ProblemAsync(uncovered, HttpStatusCode.NotFound, "not_found");
            (await proof.AuditAsync(forbidden, SeedData.English)).Should().BeEmpty();
            (await proof.AuditAsync(uncovered, SeedData.English)).Should().BeEmpty();
            proof.Observation.DirectoryReads.Should().Be(0);
        }
        finally { await RemoveAuditTriggerAsync(proof); }
    }

    private const long HttpCommitGate = 7_601_077_005;
    private static async Task InstallAuditTriggerAsync(IdentityAuthorizationHttpProof proof, string operation, bool deferred, bool gate)
    {
        // Fixed test-owned operation strings; no request data enters fixture DDL.
        var action = gate ? $"PERFORM pg_catalog.pg_advisory_xact_lock({HttpCommitGate});"
            : "RAISE EXCEPTION 'HTTP audit fixture refusal' USING ERRCODE='P0399';";
        await proof.OwnerAsync($$"""
            CREATE FUNCTION public.fn_http_authorization_audit_proof() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN
                IF NEW.operation='{{operation}}' THEN {{action}} END IF;
                RETURN NEW;
            END $proof$;
            """);
        await proof.OwnerAsync(deferred ? """
            CREATE CONSTRAINT TRIGGER tg_http_authorization_audit_proof AFTER INSERT ON public.audit_log
            DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.fn_http_authorization_audit_proof()
            """ : """
            CREATE TRIGGER tg_http_authorization_audit_proof BEFORE INSERT ON public.audit_log
            FOR EACH ROW EXECUTE FUNCTION public.fn_http_authorization_audit_proof()
            """);
    }

    private static Task RemoveAuditTriggerAsync(IdentityAuthorizationHttpProof proof) => proof.OwnerAsync("""
        DROP TRIGGER IF EXISTS tg_http_authorization_audit_proof ON public.audit_log;
        DROP FUNCTION IF EXISTS public.fn_http_authorization_audit_proof();
        """);

    private sealed class FixedBudgetClock : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => 0;
    }
}
