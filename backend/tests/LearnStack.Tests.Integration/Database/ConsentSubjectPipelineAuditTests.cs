using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Test-only subject capture; the production HTTP/JWT producer is outside this proof.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class ConsentSubjectPipelineAuditTests(IdentityMembershipFixture fixture) : IClassFixture<IdentityMembershipFixture>
{
    [Fact]
    public async Task Captured_entry_writes_must_audit_on_commit_and_rolls_back_on_real_audit_fault()
    {
        var actor = UserId.From(IdentityMembershipFixture.PersonA);
        var tenant = TenantId.From(SchemaFixture.TenantA);
        var branch = OrganizationId.From(SchemaFixture.OrgA2);
        var member = IdentityMembershipFixture.BranchMembershipA2;
        var first = Guid.CreateVersion7();
        var faultTarget = Guid.CreateVersion7();
        await new ConsentSubjectCapturedOwnerTests(fixture).SeedAndEndLifecycleAsync(first, Guid.CreateVersion7(), actor, tenant, branch, member);
        await SeedSecondGrantAsync(faultTarget, actor, tenant, branch, member);
        var subject = new GlobalIdentitySubject(IdentityMembershipFixture.Authority.AuthorityId,
            IdentityMembershipFixture.Authority.Issuer, "fixture-subject-" + actor.Value.ToString("N"));
        var scope = await LookupAsync(actor, tenant, branch, member);

        var originalEvidence = first;
        var firstRequest = new WithdrawOwnMembershipFieldConsentCommand(first, 1, first, true);
        await using (var pipeline = ConsentSubjectPipelineHarness.Create(fixture.Postgres.AppConnectionString, subject,
            actor, tenant, branch, scope, "consent-audit-success"))
        {
            var result = await pipeline.Sender.Send(firstRequest);
            result.IsSuccess.Should().BeTrue(result.Error?.Code ?? "success");
            result.Value!.Sequence.Should().Be(2);
            result.Value.IsGranted.Should().BeFalse();
            first = result.Value.EvidenceId;
        }
        (await CountAuditAsync("consent-audit-success", first)).Should().Be(1);
        (await CountTupleAsync(member, actor, tenant, branch, first)).Should().Be(2);

        await using (var pipeline = ConsentSubjectPipelineHarness.Create(fixture.Postgres.AppConnectionString, subject,
            actor, tenant, branch, scope, "consent-audit-unchanged"))
        {
            var result = await pipeline.Sender.Send(new WithdrawOwnMembershipFieldConsentCommand(first, 2, first, true));
            result.IsSuccess.Should().BeTrue(result.Error?.Code ?? "success");
            result.Value!.EvidenceId.Should().Be(first);
            result.Value.Sequence.Should().Be(2);
        }
        (await CountAuditAsync("consent-audit-unchanged", first)).Should().Be(1,
            "same-state convergence still classifies and names the existing MUST audit subject");
        (await CountTupleAsync(member, actor, tenant, branch, first)).Should().Be(2);

        await using (var pipeline = ConsentSubjectPipelineHarness.Create(fixture.Postgres.AppConnectionString, subject,
            actor, tenant, branch, scope, "consent-audit-stale"))
        {
            var stale = await pipeline.Sender.Send(new WithdrawOwnMembershipFieldConsentCommand(originalEvidence, 1, originalEvidence, true));
            stale.IsFailure.Should().BeTrue();
            stale.Error!.Code.Should().Be("concurrency_conflict");
        }
        (await CountTupleAsync(member, actor, tenant, branch, first)).Should().Be(2);
        await using (var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.PlatformConnectionString))
        {
            (await IdentityMembershipFixture.ScalarAsync<long>(platform, null, """
                SELECT count(*) FROM public.audit_log WHERE correlation_id='consent-audit-stale'
                  AND operation='tenancy.membership_field_consent.withdraw' AND outcome='failed'
                """)).Should().Be(1, "the refused stale withdrawal has its own durable MUST audit outcome");
        }

        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.MigrationConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(owner, null, """
            CREATE FUNCTION public.fn_consent_audit_test_fault() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
              IF NEW.operation='tenancy.membership_field_consent.withdraw' AND NEW.correlation_id='consent-audit-fault' THEN
                RAISE EXCEPTION 'fixed consent audit test refusal' USING ERRCODE='23514';
              END IF;
              RETURN NEW;
            END $$;
            CREATE TRIGGER tg_consent_audit_test_fault BEFORE INSERT ON public.audit_log
              FOR EACH ROW EXECUTE FUNCTION public.fn_consent_audit_test_fault();
            """);
        try
        {
            await using var pipeline = ConsentSubjectPipelineHarness.Create(fixture.Postgres.AppConnectionString, subject,
                actor, tenant, branch, scope, "consent-audit-fault");
            var send = () => pipeline.Sender.Send(new WithdrawOwnMembershipFieldConsentCommand(faultTarget, 1, faultTarget, true));
            await send.Should().ThrowAsync<AuditWriteFailedException>();
            (await CountTupleAsync(member, actor, tenant, branch, faultTarget)).Should().Be(1,
                "the new withdrawal and its failed MUST audit share the same rolled-back transaction");
            (await CountAuditByCorrelationAsync("consent-audit-fault")).Should().Be(0);
        }
        finally
        {
            await IdentityMembershipFixture.ExecuteAsync(owner, null, """
                DROP TRIGGER tg_consent_audit_test_fault ON public.audit_log;
                DROP FUNCTION public.fn_consent_audit_test_fault();
                """);
        }
    }

    private async Task<ConsentSubjectScopeObservation> LookupAsync(UserId actor, TenantId tenant, OrganizationId branch, Guid member)
    {
        await using var source = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        var services = new ServiceCollection();
        services.AddLogging(); services.AddSingleton(source); services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddScoped<IConsentSubjectScopeLookup, ConsentSubjectScopeLookup>();
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var lookup = scope.ServiceProvider.GetRequiredService<IConsentSubjectScopeLookup>();
        var result = await lookup.LookupAsync(actor, tenant, branch, ConsentSubjectLookupMode.Select, member, null, 1);
        scope.ServiceProvider.GetRequiredService<IUnitOfWork>().HasActiveTransaction.Should().BeFalse();
        return result.Should().ContainSingle().Subject;
    }

    private async Task SeedSecondGrantAsync(Guid id, UserId actor, TenantId tenant, OrganizationId branch, Guid member)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var tx = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, tx, tenant.Value, branch.Value);
        await IdentityMembershipFixture.LockAsync(app, tx, tenant.Value, null, true);
        await IdentityMembershipFixture.ExecuteAsync(app, tx, """
            INSERT INTO public.membership_field_consents
              (id,tenant_id,organization_id,membership_id,subject_user_id,actor_user_id,definition_id,definition_version,
               purpose_key,notice_locale,notice_hash,sequence,state,predecessor_sequence,created_at,created_by,row_version)
            VALUES (@id,@tenant,@organization,@member,@actor,@actor,@definition,1,'test-purpose','en',
              'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',1,'Granted',0,clock_timestamp(),@actor,0)
            """, ("id", id), ("tenant", tenant.Value), ("organization", branch.Value), ("member", member),
            ("actor", actor.Value), ("definition", Guid.CreateVersion7()));
        await tx.CommitAsync();
    }

    private async Task<long> CountAuditAsync(string correlation, Guid subject)
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.PlatformConnectionString);
        return await IdentityMembershipFixture.ScalarAsync<long>(platform, null, """
            SELECT count(*) FROM public.audit_log WHERE correlation_id=@correlation AND operation='tenancy.membership_field_consent.withdraw'
              AND outcome='success' AND entity_type='MembershipFieldConsent' AND entity_id=@subject
            """, ("correlation", correlation), ("subject", subject.ToString("D")));
    }

    private async Task<long> CountAuditByCorrelationAsync(string correlation)
    {
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.PlatformConnectionString);
        return await IdentityMembershipFixture.ScalarAsync<long>(platform, null,
            "SELECT count(*) FROM public.audit_log WHERE correlation_id=@correlation", ("correlation", correlation));
    }

    private async Task<long> CountTupleAsync(Guid member, UserId actor, TenantId tenant, OrganizationId branch, Guid evidence)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await using var tx = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, tx, tenant.Value, branch.Value);
        var count = await IdentityMembershipFixture.ScalarAsync<long>(app, tx, """
            SELECT count(*) FROM public.membership_field_consents c
            WHERE c.membership_id=@member AND c.subject_user_id=@actor AND c.definition_id=(
              SELECT definition_id FROM public.membership_field_consents WHERE id=@evidence)
            """, ("member", member), ("actor", actor.Value), ("evidence", evidence));
        await tx.RollbackAsync();
        return count;
    }
}
