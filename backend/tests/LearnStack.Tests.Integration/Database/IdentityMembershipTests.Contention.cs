using System.Diagnostics;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Application.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class IdentityMembershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Post_acquisition_row_timeout_is_a_fixed_conflict_and_the_seed_handler_refuses_without_partial_profile(bool throughHandler)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await SeedSchemaAsync(database.MigrationConnectionString, database.AppConnectionString);
        var tenant = TenantId.From(SchemaFixture.TenantA);
        await SeedFoundationTenantAsync(database.AppConnectionString, tenant);
        await using var blocker = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using (var preparation = await blocker.BeginTransactionAsync())
        {
            await ScopeAsync(blocker, preparation, tenant.Value);
            await LockAsync(blocker, preparation, tenant.Value, null, false);
            await ExecuteAsync(blocker, preparation, """
                UPDATE public.membership_profiles SET display_name=NULL,preferred_locale=NULL WHERE membership_id=@member;
                SET CONSTRAINTS ALL IMMEDIATE;
                """, ("member", LearnerMembershipA));
            await preparation.CommitAsync();
        }
        await using var held = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, held, tenant.Value);
        await LockAsync(blocker, held, tenant.Value, null, false);
        (await ScalarAsync<Guid>(blocker, held, "SELECT id FROM public.memberships WHERE id=@member FOR NO KEY UPDATE",
            ("member", LearnerMembershipA))).Should().Be(LearnerMembershipA);

        await using var source = ApplicationDataSource.Build(database.AppConnectionString);
        await using var unit = new NpgsqlUnitOfWork(source, NullLogger<NpgsqlUnitOfWork>.Instance);
        await using var frame = await unit.BeginTransactionAsync();
        await unit.SetProvisioningTenantContextAsync(tenant);
        var context = new FoundationProofContext(tenant);
        await using var db = new IdentityDbContext(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(unit.Connection).Options,
            new StaticTenantContextAccessor(context), EmptyIdentityScope.Instance);
        await db.Database.UseTransactionAsync(unit.Transaction);
        var store = new MembershipWriteStore(db, new ContextualIdentityStorageControl(db, unit));
        var locks = new IdentityMutationLocks(unit);
        Task<Result<None>>? handled = null;
        Task pending;
        var started = Stopwatch.StartNew();
        if (throughHandler)
        {
            var role = RoleId(tenant.Value, BuiltInRoleKey.Learner, PermissionScope.Tenant);
            var fingerprint = IdentityFoundationManifest.Version1.ProfileFingerprint(tenant, null, LearnerMembershipA, "timeout-proof", "en");
            var command = new SetSeedMembershipProfileCommand(LearnerMembershipA, UserId.From(PersonA), role,
                PermissionScope.Tenant, "timeout-proof", "en", 1, fingerprint);
            var admission = new FoundationProofAdmission(new SeedIdentityFoundationAct(SeedIdentityFoundationOperation.SetProfile, tenant, null,
                UserId.SystemActor, LearnerMembershipA, UserId.From(PersonA), role, null, PermissionScope.Tenant, 1, fingerprint));
            var handler = new SetSeedMembershipProfileCommandHandler(store, context, admission,
                new IdentityScopeLifecycleReader(unit), locks, new SystemClock(), new RoleReadStore(db),
                new ContentionSeedContinuation());
            handled = handler.Handle(command, CancellationToken.None);
            pending = handled;
        }
        else
        {
            await locks.AcquireAsync(new([], tenant, IdentityTenantLockMode.Shared));
            var membership = (await store.FindAsync(MembershipId.From(LearnerMembershipA)))!;
            membership.SetProfile("timeout-proof", "en", DateTimeOffset.UtcNow, UserId.SystemActor).IsSuccess.Should().BeTrue();
            pending = store.UpdateAsync(membership);
        }
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
        await WaitForBlockingBackendAsync(observer, ((NpgsqlConnection)unit.Connection).ProcessID, blocker.ProcessID);
        pending.IsCompleted.Should().BeFalse("the actual UPDATE, after advisory acquisition, is waiting on the Membership row");
        if (throughHandler)
        {
            var result = await handled!;
            result.IsFailure.Should().BeTrue();
            result.Error!.Code.Should().Be("identity_foundation_refused");
        }
        else
        {
            var failure = (await FluentActions.Awaiting(() => pending).Should().ThrowAsync<IdentityFoundationStorageException>()).Which;
            failure.Failure.Should().Be(IdentityFoundationStorageFailure.Conflict);
            failure.InnerException.Should().BeNull();
        }
        started.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(1.5)).And.BeLessThan(TimeSpan.FromSeconds(8));
        unit.IsRollbackOnly.Should().BeTrue();
        db.Database.AutoSavepointsEnabled.Should().BeTrue();
        await frame.FailAsync();
        await held.RollbackAsync();
        await using var read = await blocker.BeginTransactionAsync();
        await ScopeAsync(blocker, read, tenant.Value);
        (await ScalarAsync<long>(blocker, read, """
            SELECT count(*) FROM public.memberships member
            JOIN public.membership_profiles profile ON profile.tenant_id=member.tenant_id AND profile.membership_id=member.id
            WHERE member.id=@member AND member.row_version=0 AND profile.display_name IS NULL AND profile.preferred_locale IS NULL
            """, ("member", LearnerMembershipA))).Should().Be(1);
    }

    private static async Task SeedFoundationTenantAsync(string connectionString, TenantId tenant)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(connectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, tenant.Value);
        await ExecuteAsync(app, transaction, """
            INSERT INTO public.tenants(id,slug,display_name,status,created_at,created_by,row_version)
            VALUES(@tenant,@slug,'Foundation proof','Trial',clock_timestamp(),@actor,0)
            """, ("tenant", tenant.Value), ("slug", "foundation-" + tenant.Value.ToString("N")), ("actor", UserId.SystemActor.Value));
        await transaction.CommitAsync();
    }

    private sealed class FoundationProofAdmission(params SeedIdentityFoundationAct[] permitted) : ISeedIdentityFoundationAdmission
    {
        public ValueTask<bool> AllowsFoundationAsync(SeedIdentityFoundationAct act, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(permitted.Contains(act));
        public ValueTask<bool> AllowsAsync(GlobalIdentitySubject subject, UserId declaredActor, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(false);
    }

    // This fixture isolates actual row-lock contention; fixed declaration admission is covered by Seeder tests.
    private sealed class ContentionSeedContinuation : IFixedSeedPrerequisiteVerifier
    {
        public Task<bool> VerifyAsync(object request, IdentityMutationLockPlan plan, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);
    }

    private sealed class FoundationProofContext(TenantId tenant) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;
        public TenantId TenantId => tenant;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public string? CorrelationId => null;
        public string? ModuleName => "identity";
    }
}
