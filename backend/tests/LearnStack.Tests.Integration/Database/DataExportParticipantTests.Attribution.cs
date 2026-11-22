using FluentAssertions;
using LearnStack.Modules.Customization.Infrastructure.DataRequests;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.Modules.Tenancy.Infrastructure.DataRequests;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// ADR-0107 § 6 against the database: <c>attribution_reference</c> counts rows, any <c>UserId</c> attribution column
/// included, exactly in the group.
/// </summary>
public sealed partial class DataExportParticipantTests
{
    [Fact]
    public async Task Attribution_counts_a_row_once_whatever_columns_name_the_subject_and_counts_a_non_auditable_updater()
    {
        await using var scope = await DataExportTestKit.OpenAsync(schema.Postgres.AppConnectionString, SchemaFixture.TenantA, null);
        await using var customization = new CustomizationDbContext(new DbContextOptionsBuilder<CustomizationDbContext>()
            .UseNpgsql(scope.Connection).Options, new StaticTenantContextAccessor(scope.Context));
        await customization.Database.UseTransactionAsync(scope.Transaction);
        await using var tenancy = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(scope.Connection).Options, new StaticTenantContextAccessor(scope.Context));
        await tenancy.Database.UseTransactionAsync(scope.Transaction);
        var subject = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self, UserId.From(PersonA),
            new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), null), [LearnerMembershipA]);
        var definitions = new CustomizationUserDataParticipant(customization, scope.Context);
        var settings = new TenancyUserDataParticipant(tenancy, scope.Context);
        var other = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self, UserId.From(AdminA),
            new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), null), [AdminMembershipA]);
        var otherBefore = Attribution(await definitions.DescribeRetainedAsync(other, CancellationToken.None))
            + Attribution(await settings.DescribeRetainedAsync(other, CancellationToken.None));
        var definitionsBefore = Attribution(await definitions.DescribeRetainedAsync(subject, CancellationToken.None));
        var settingsBefore = Attribution(await settings.DescribeRetainedAsync(subject, CancellationToken.None));

        // A definition the subject created and later updated: two attribution columns, one retained row.
        var definition = Guid.CreateVersion7();
        await ExecuteAsync(scope.Connection, scope.Transaction,
            "SELECT pg_advisory_xact_lock(public.fn_identity_contextual_lock_key('tenant', @tenant))", ("tenant", SchemaFixture.TenantA));
        await ExecuteAsync(scope.Connection, scope.Transaction, """
            INSERT INTO public.tenant_custom_field_defs
                (id,tenant_id,target_entity,key,schema_version,status,json_schema,labels_json,pii_category,
                 is_required,sort,maximum_value_bytes,purpose_key,notices_json,created_at,created_by,row_version)
            VALUES(@definition,@tenant,'Membership','attribution-proof',1,'Draft',
                '{"type":"object","properties":{"value":{"type":"boolean"}},"required":["value"],"additionalProperties":false}',
                '{"tr-TR":"Attribution proof"}','None',false,0,64,NULL,NULL,clock_timestamp(),@person,0);
            UPDATE public.tenant_custom_field_defs SET status='Active',row_version=1,updated_at=clock_timestamp(),updated_by=@person
                WHERE id=@definition;
            """, ("definition", definition), ("tenant", SchemaFixture.TenantA), ("person", PersonA));

        // A feature flag the subject last changed: an UpdatedBy outside the AuditableEntity set.
        await ExecuteAsync(scope.Connection, scope.Transaction, """
            INSERT INTO public.tenant_feature_flags (tenant_id, key, value, updated_by)
            VALUES (@tenant, 'attribution.proof', 'true', @person)
            """, ("tenant", SchemaFixture.TenantA), ("person", PersonA));

        Attribution(await definitions.DescribeRetainedAsync(subject, CancellationToken.None)).Should().Be(definitionsBefore + 1,
            "the unit is the row: created and updated by the subject is one retained row");
        Attribution(await settings.DescribeRetainedAsync(subject, CancellationToken.None)).Should().Be(settingsBefore + 1,
            "a UserId attribution column counts whether or not the entity is an AuditableEntity");
        (Attribution(await definitions.DescribeRetainedAsync(other, CancellationToken.None))
            + Attribution(await settings.DescribeRetainedAsync(other, CancellationToken.None))).Should().Be(otherBefore,
            "another person's count is untouched by the subject's rows");
        await scope.Frame.FailAsync();
    }

    [Fact]
    public async Task A_request_receipt_counts_for_whoever_filed_or_cancelled_it_once_per_row()
    {
        await using var scope = await DataExportTestKit.OpenAsync(schema.Postgres.AppConnectionString, SchemaFixture.TenantA,
            SchemaFixture.OrgA1, [PersonA]);
        // The retained report reads only; the erasure service is never reached here.
        var participant = new IdentityUserDataParticipant(scope.Db, scope.Context, new MembershipFieldRevisionReader(null!), null!);
        var group = new DataRequestScopeGroup(TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1));
        var person = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self, UserId.From(PersonA),
            group, [BranchMembershipA1]);
        var administrator = new UserDataSubject(Guid.CreateVersion7(), DataRequestKind.Export, DataRequestOrigin.Self,
            UserId.From(AdminA), group, [Guid.CreateVersion7()]);
        var personBefore = Receipts(await participant.DescribeRetainedAsync(person, CancellationToken.None));
        var administratorBefore = await participant.DescribeRetainedAsync(administrator, CancellationToken.None);

        // The person's own request, cancelled by the administrator: the administrator is only its updater.
        var own = Guid.CreateVersion7();
        await DataRequestSchemaSeed.InsertRequestAsync(scope.Connection, scope.Transaction, own, SchemaFixture.TenantA, SchemaFixture.OrgA1,
            PersonA, [BranchMembershipA1]);
        await ExecuteAsync(scope.Connection, scope.Transaction, """
            UPDATE public.data_subject_requests SET status='cancelled',row_version=row_version+1,updated_at=now(),updated_by=@admin
            WHERE id=@id
            """, ("id", own), ("admin", AdminA));

        // The administrator's request about the person, which the administrator then cancels: requester, creator and
        // updater all name the administrator, and it is still one row.
        var filed = Guid.CreateVersion7();
        await ExecuteAsync(scope.Connection, scope.Transaction, """
            INSERT INTO public.data_subject_requests(id,tenant_id,organization_id,kind,origin,subject_user_id,requester_user_id,
                status,membership_snapshot,created_at,created_by)
            VALUES(@id,@tenant,@organization,'export','administrator',@subject,@admin,'requested',@snapshot::jsonb,now(),@admin);
            UPDATE public.data_subject_requests SET status='cancelled',row_version=row_version+1,updated_at=now(),updated_by=@admin
            WHERE id=@id;
            """, ("id", filed), ("tenant", SchemaFixture.TenantA), ("organization", SchemaFixture.OrgA1), ("subject", PersonA),
            ("admin", AdminA), ("snapshot", DataRequestSchemaSeed.Snapshot(BranchMembershipA1)));

        var administratorAfter = await participant.DescribeRetainedAsync(administrator, CancellationToken.None);
        Receipts(administratorAfter).Should().Be(Receipts(administratorBefore) + 2,
            "a request the administrator filed or cancelled survives naming them, once per row");
        Attribution(administratorAfter).Should().Be(Attribution(administratorBefore),
            "the receipts are reported as request_receipt and never as attribution too");
        Receipts(await participant.DescribeRetainedAsync(person, CancellationToken.None)).Should().Be(personBefore + 2,
            "both requests are about the person");
        await scope.Frame.FailAsync();
    }

    private static long Receipts(IReadOnlyCollection<RetainedReferenceCount> retained) =>
        retained.Single(item => item.Kind == RetainedReferenceKind.RequestReceipt).Count;

    private static long Attribution(IReadOnlyCollection<RetainedReferenceCount> retained) =>
        retained.Single(item => item.Kind == RetainedReferenceKind.AttributionReference).Count;
}
