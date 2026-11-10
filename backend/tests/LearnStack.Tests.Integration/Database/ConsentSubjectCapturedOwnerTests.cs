using System.Collections.Immutable;
using FluentAssertions;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Application.FieldConsents;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Test-only captured provenance; these tests do not claim HTTP credential admission.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class ConsentSubjectCapturedOwnerTests(IdentityMembershipFixture fixture) : IClassFixture<IdentityMembershipFixture>
{
    [Fact]
    public async Task Captured_scope_survives_lifecycle_loss_and_each_actual_owner_stays_bounded()
    {
        var actor = UserId.From(IdentityMembershipFixture.PersonA);
        var tenant = TenantId.From(SchemaFixture.TenantA);
        var branch = OrganizationId.From(SchemaFixture.OrgA2);
        var member = IdentityMembershipFixture.BranchMembershipA2;
        var evidence = Guid.CreateVersion7();
        var definition = Guid.CreateVersion7();
        await SeedAndEndLifecycleAsync(evidence, definition, actor, tenant, branch, member);

        await using var dataSource = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(dataSource);
        services.AddScoped<IUnitOfWork, NpgsqlUnitOfWork>();
        services.AddScoped<IConsentSubjectScopeLookup, ConsentSubjectScopeLookup>();
        await using var provider = services.BuildServiceProvider();
        await using var preliminary = provider.CreateAsyncScope();
        var lookup = preliminary.ServiceProvider.GetRequiredService<IConsentSubjectScopeLookup>();
        var ambient = preliminary.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var retained = await lookup.LookupAsync(actor, tenant, branch, ConsentSubjectLookupMode.Select, member, null, 1);
        retained.Should().ContainSingle();
        retained[0].MembershipId.Should().Be(member);
        ambient.HasActiveTransaction.Should().BeFalse("the preliminary read must be disposed before a subject owner begins");
        (await lookup.LookupAsync(actor, tenant, OrganizationId.From(SchemaFixture.OrgA1),
            ConsentSubjectLookupMode.Select, member, null, 1)).Should().BeEmpty();
        (await lookup.LookupAsync(actor, tenant, branch, ConsentSubjectLookupMode.List, null, null, 20))
            .Select(scope => scope.MembershipId).Should().Contain(member);

        var subject = new GlobalIdentitySubject(IdentityMembershipFixture.Authority.AuthorityId,
            IdentityMembershipFixture.Authority.Issuer, "fixture-subject-" + actor.Value.ToString("N"));
        foreach (var purpose in new[] { ConsentSubjectPurpose.Discover, ConsentSubjectPurpose.History })
        {
            var selected = purpose == ConsentSubjectPurpose.Discover ? null : retained[0];
            var context = TenantContextFactory.CreateForConsentSubjectScope(tenant, branch, actor, selected, null).Value!;
            var operation = purpose == ConsentSubjectPurpose.Discover
                ? (object)new ListOwnFieldConsentScopesQuery() : new ListOwnMembershipFieldConsentsQuery(member);
            var policy = purpose == ConsentSubjectPurpose.Discover
                ? OperationPolicy.ForOwnConsentEntry<ListOwnFieldConsentScopesQuery>("test.consent.scopes")
                : OperationPolicy.ForOwnConsentEntry<ListOwnMembershipFieldConsentsQuery>("test.consent.history");
            using var candidate = new CandidateSource(context);
            var invocations = new InvocationSource();
            var invocation = invocations.Open(operation, policy, context, candidate.Create());
            using var source = new WitnessSource(subject, actor, tenant, branch, purpose, selected,
                purpose == ConsentSubjectPurpose.Discover ? new ConsentSubjectScopePage([.. retained], null) : null);
            var witness = source.ForInvocation(invocation)!;
            await using var unit = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance);
            await using var frame = await unit.BeginConsentSubjectTransactionAsync(witness, context);
            unit.IsConsentSubjectOwner(witness).Should().BeTrue();
            unit.Mode.Should().Be(TransactionMode.ReadOnly);
            (await ScalarAsync<string>(unit, "SELECT current_setting('transaction_read_only')")).Should().Be("on");
            (await ScalarAsync<string>(unit, "SELECT current_setting('app.tenant_id')")).Should().Be(tenant.Value.ToString("D"));
            (await ScalarAsync<string>(unit, "SELECT current_setting('app.organization_id')")).Should().Be(branch.Value.ToString("D"));
            (await ScalarAsync<string>(unit, "SELECT COALESCE(NULLIF(current_setting('app.scope',true),''),'none')")).Should().Be("none");
            if (purpose == ConsentSubjectPurpose.History)
            {
                await using var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
                    .UseNpgsql(unit.Connection).Options, new StaticTenantContextAccessor(context));
                await db.Database.UseTransactionAsync(unit.Transaction);
                var history = new OwnMembershipFieldConsentHistory(db, context);
                (await history.ReadEvidenceAsync(actor, member, evidence))!.EvidenceId.Should().Be(evidence);
            }
            await frame.CompleteAsync();
            unit.IsConsentSubjectOwner(witness).Should().BeFalse();
        }

        // A new application invocation receives a fresh single-use witness and real RW owner.
        await WithdrawThroughCapturedOwnerAsync(dataSource, subject, actor, tenant, branch, retained[0], evidence);
    }

    [Fact]
    public async Task Captured_owner_rejects_http_entry_mismatched_context_reentry_and_lost_liveness()
    {
        var actor = UserId.From(IdentityMembershipFixture.PersonA);
        var tenant = TenantId.From(SchemaFixture.TenantA);
        var branch = OrganizationId.From(SchemaFixture.OrgA1);
        var selected = new ConsentSubjectScopeObservation(IdentityMembershipFixture.BranchMembershipA1, tenant, branch);
        var subject = new GlobalIdentitySubject(IdentityMembershipFixture.Authority.AuthorityId,
            IdentityMembershipFixture.Authority.Issuer, "fixture-subject-" + actor.Value.ToString("N"));
        var context = TenantContextFactory.CreateForConsentSubjectScope(tenant, branch, actor, selected, null).Value!;
        var sameValuesButDifferentContext = TenantContextFactory.CreateForConsentSubjectScope(tenant, branch, actor, selected, null).Value!;
        var request = new ListOwnMembershipFieldConsentsQuery(selected.MembershipId);
        var policy = OperationPolicy.ForOwnConsentEntry<ListOwnMembershipFieldConsentsQuery>("test.consent.history");
        await using var dataSource = ApplicationDataSource.Build(fixture.Postgres.AppConnectionString);

        foreach (var http in new[] { true, false })
        {
            using var candidate = new CandidateSource(context);
            var invocations = new InvocationSource();
            var invocation = invocations.Open(request, policy, context, candidate.Create(),
                http ? OperationEntryKind.Http : OperationEntryKind.Application);
            using var source = new WitnessSource(subject, actor, tenant, branch, ConsentSubjectPurpose.History, selected, null);
            var witness = source.ForInvocation(invocation)!;
            await using var unit = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance);
            var begin = () => unit.BeginConsentSubjectTransactionAsync(witness, http ? context : sameValuesButDifferentContext);
            await begin.Should().ThrowAsync<InvalidOperationException>();
            unit.IsConsentSubjectOwner(witness).Should().BeFalse();
        }

        using (var candidate = new CandidateSource(context))
        {
            var invocations = new InvocationSource();
            var invocation = invocations.Open(request, policy, context, candidate.Create());
            using var source = new WitnessSource(subject, actor, tenant, branch, ConsentSubjectPurpose.History, selected, null);
            var witness = source.ForInvocation(invocation)!;
            await using var unit = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance);
            await using var frame = await unit.BeginConsentSubjectTransactionAsync(witness, context);
            var nested = () => unit.BeginTransactionAsync(TransactionMode.ReadOnly);
            await nested.Should().ThrowAsync<InvalidOperationException>();
            unit.IsConsentSubjectOwner(witness).Should().BeFalse();
            await frame.FailAsync();
        }
        using (var candidate = new CandidateSource(context))
        {
            var invocations = new InvocationSource();
            var invocation = invocations.Open(request, policy, context, candidate.Create());
            using var source = new WitnessSource(subject, actor, tenant, branch, ConsentSubjectPurpose.History, selected, null);
            var witness = source.ForInvocation(invocation)!;
            await using var unit = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance);
            await using var frame = await unit.BeginConsentSubjectTransactionAsync(witness, context);
            source.Dispose();
            unit.IsConsentSubjectOwner(witness).Should().BeFalse();
            Action useClosed = () => _ = unit.Connection;
            useClosed.Should().Throw<InvalidOperationException>();
            await frame.FailAsync();
        }
        // A returned pooled connector must have no retained tenant or org authority.
        await using var pooled = await dataSource.OpenConnectionAsync();
        await using var check = new NpgsqlCommand("""
            SELECT NULLIF(current_setting('app.tenant_id',true), '') IS NULL
               AND NULLIF(current_setting('app.organization_id',true), '') IS NULL
               AND NULLIF(current_setting('app.scope',true), '') IS NULL
            """, pooled);
        (await check.ExecuteScalarAsync()).Should().Be(true);
    }

    private static async Task WithdrawThroughCapturedOwnerAsync(NpgsqlDataSource dataSource, GlobalIdentitySubject subject,
        UserId actor, TenantId tenant, OrganizationId branch, ConsentSubjectScopeObservation selected, Guid original)
    {
        var first = await WithdrawAsync(dataSource, subject, actor, tenant, branch, selected, original, 1, original);
        first.IsSuccess.Should().BeTrue();
        first.Value!.IsGranted.Should().BeFalse();
        first.Value.Sequence.Should().Be(2);
        var same = await WithdrawAsync(dataSource, subject, actor, tenant, branch, selected, original, 2, first.Value.EvidenceId);
        same.IsSuccess.Should().BeTrue();
        same.Value!.EvidenceId.Should().Be(first.Value.EvidenceId);
        var stale = await WithdrawAsync(dataSource, subject, actor, tenant, branch, selected, original, 1, original);
        stale.IsFailure.Should().BeTrue();
        stale.Error!.Code.Should().Be("concurrency_conflict");
    }

    private static async Task<LearnStack.SharedKernel.Results.Result<MembershipFieldConsentReceipt>> WithdrawAsync(
        NpgsqlDataSource dataSource, GlobalIdentitySubject subject, UserId actor, TenantId tenant,
        OrganizationId branch, ConsentSubjectScopeObservation selected, Guid original, long sequence, Guid expected)
    {
        var context = TenantContextFactory.CreateForConsentSubjectScope(tenant, branch, actor, selected, null).Value!;
        var request = new WithdrawOwnMembershipFieldConsentCommand(original, sequence, expected, true);
        var catalog = new OperationPolicyCatalog([new TenancyOperationPolicySource()]);
        catalog.TryGet(request.GetType(), out var policy).Should().BeTrue();
        using var candidate = new CandidateSource(context);
        var invocations = new InvocationSource();
        var invocation = invocations.Open(request, policy, context, candidate.Create());
        using var source = new WitnessSource(subject, actor, tenant, branch, ConsentSubjectPurpose.Withdraw, selected, null);
        var witness = source.ForInvocation(invocation)!;
        invocations.Allow(DecisionSource.Allow(invocation, invocation.Candidate!, witness));
        await using var unit = new NpgsqlUnitOfWork(dataSource, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginConsentSubjectTransactionAsync(witness, context);
        unit.Mode.Should().Be(TransactionMode.ReadWrite);
        (await ScalarAsync<string>(unit, "SELECT current_setting('transaction_read_only')")).Should().Be("off");
        await using var db = new TenancyDbContext(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql(unit.Connection).Options, new StaticTenantContextAccessor(context));
        await db.Database.UseTransactionAsync(unit.Transaction);
        var locks = new IdentityMutationLocks(unit);
        var handler = new WithdrawOwnMembershipFieldConsentCommandHandler(
            new OwnConsentEntryAuthorization(invocations, catalog, unit, context),
            new OwnMembershipFieldConsentHistory(db, context), new MembershipFieldConsentWriteStore(db, locks, unit),
            locks, new AuditStateCapture(), new SystemClock(), new SystemGuidFactory());
        var result = await handler.Handle(request, CancellationToken.None);
        if (result.IsSuccess) await frame.CompleteAsync(); else await frame.FailAsync();
        return result;
    }

    internal async Task SeedAndEndLifecycleAsync(Guid evidence, Guid definition, UserId actor, TenantId tenant,
        OrganizationId branch, Guid membership)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(fixture.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.Value);
        await IdentityMembershipFixture.LockAsync(app, transaction, tenant.Value, actor.Value, true);
        // IdentityMembershipFixture owns contextual rows; create this real tenant and
        // its two branches here before proving lifecycle-loss recovery.
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version)
              VALUES (@tenant,'consent-test','Consent test','Trial',clock_timestamp(),@actor,0);
            INSERT INTO public.organizations(id,tenant_id,slug,display_name,status,created_at,created_by,row_version)
              VALUES (@main,@tenant,'main','Main','Active',clock_timestamp(),@actor,0),
                     (@organization,@tenant,'branch','Branch','Active',clock_timestamp(),@actor,0);
            UPDATE public.tenants SET default_organization_id=@main WHERE id=@tenant;
            """, ("tenant", tenant.Value), ("main", SchemaFixture.OrgA1), ("organization", branch.Value), ("actor", actor.Value));
        await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.Value, branch.Value);
        await IdentityMembershipFixture.ExecuteAsync(app, transaction, """
            INSERT INTO public.membership_field_consents
              (id,tenant_id,organization_id,membership_id,subject_user_id,actor_user_id,definition_id,definition_version,
               purpose_key,notice_locale,notice_hash,sequence,state,predecessor_sequence,created_at,created_by,row_version)
            VALUES (@evidence,@tenant,@organization,@membership,@actor,@actor,@definition,1,'test-purpose','en',
              'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',1,'Granted',0,clock_timestamp(),@actor,0);
            UPDATE public.memberships SET status='Revoked',deleted_at=clock_timestamp(),deleted_by=@actor WHERE id=@membership;
            UPDATE public.organizations SET status='Archived' WHERE id=@organization;
            UPDATE public.tenants SET status='Suspended' WHERE id=@tenant;
            """, ("evidence", evidence), ("tenant", tenant.Value), ("organization", branch.Value),
            ("membership", membership), ("actor", actor.Value), ("definition", definition));
        (await IdentityMembershipFixture.ScalarAsync<string>(app, transaction, "SELECT status FROM public.memberships WHERE id=@id", ("id", membership))).Should().Be("Revoked");
        (await IdentityMembershipFixture.ScalarAsync<string>(app, transaction, "SELECT status FROM public.organizations WHERE id=@id", ("id", branch.Value))).Should().Be("Archived");
        (await IdentityMembershipFixture.ScalarAsync<string>(app, transaction, "SELECT status FROM public.tenants WHERE id=@id", ("id", tenant.Value))).Should().Be("Suspended");
        await transaction.CommitAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlUnitOfWork unit, string sql)
    {
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)unit.Connection, (NpgsqlTransaction)unit.Transaction!);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private sealed class CandidateSource(TenantContext context) : AuthorizationCandidate.MintingSource
    {
        internal AuthorizationCandidate Create() => ForCandidate(AuthorizationSourceKind.Http,
            context.UserId!.Value, context.TenantId, context.OrganizationId);
    }

    private sealed class InvocationSource : OperationInvocation.MintingSource, IOperationInvocationContext
    {
        private OperationInvocation? _current;
        public OperationInvocation? CurrentInvocation => _current;
        public ProtectedOperationDecision? CurrentDecision { get; private set; }
        internal OperationInvocation Open(object operation, OperationPolicy policy, TenantContext context, AuthorizationCandidate candidate,
            OperationEntryKind kind = OperationEntryKind.Application) =>
            _current = ForInvocation(operation, policy, context, candidate, null, CancellationToken.None, kind);
        internal void Allow(ProtectedOperationDecision decision) => CurrentDecision = decision;
        protected override bool IsCurrent(OperationInvocation invocation) => ReferenceEquals(_current, invocation);
    }

    private sealed class DecisionSource : ProtectedOperationDecision.MintingSource
    {
        internal static ProtectedOperationDecision Allow(OperationInvocation invocation, AuthorizationCandidate candidate,
            ConsentSubjectScopeWitness witness) => ForDecision(invocation, candidate, ProtectedOperationOutcome.Allowed, consentWitness: witness);
    }

    private sealed class WitnessSource(GlobalIdentitySubject subject, UserId actor, TenantId tenant, OrganizationId? hostOrganization,
        ConsentSubjectPurpose purpose, ConsentSubjectScopeObservation? selected, ConsentSubjectScopePage? page)
        : ConsentSubjectScopeWitness.MintingSource, IConsentSubjectScopeSource
    {
        private readonly object _evidence = new();
        private OperationInvocation? _current;
        public ConsentSubjectScopeWitness? ForInvocation(OperationInvocation invocation)
        {
            _current = invocation;
            return Capture(_evidence, invocation, subject, actor, tenant, hostOrganization,
                "consent-fixture.invalid", purpose, selected, page);
        }
        protected override bool IsEvidenceCurrent(object evidence, OperationInvocation invocation) =>
            ReferenceEquals(evidence, _evidence) && ReferenceEquals(invocation, _current);
    }
}
