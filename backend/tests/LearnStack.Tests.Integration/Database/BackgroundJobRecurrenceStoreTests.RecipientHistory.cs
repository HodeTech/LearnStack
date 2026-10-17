using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Tenancy.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BackgroundJobRecurrenceStoreTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recipient_history_resolves_real_COMMIT_loss_without_a_second_identity_or_invented_acknowledgement(bool committed)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString, dropCommitRequest: !committed);
        var ids = new RecipientIdentityCounter();
        await using var host = RecipientHost(database, new(), proxy.ConnectionString, services =>
        {
            services.RemoveAll<IGuidFactory>();
            services.AddSingleton<IGuidFactory>(ids);
        });
        var original = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        (committed ? proxy.DroppedCommitResponses : proxy.DroppedCommitRequests).Should().Be(1);
        original.Status.Should().Be(JobRecipientSubmissionStatus.Indeterminate);
        original.Command!.Gate.CommitAcknowledged.Should().BeFalse();
        original.Command.AcceptedChild.Should().BeNull();
        original.Command.ProvisionalChild.Should().NotBeNull();
        original.CleanupFailed.Should().BeFalse();
        original.SourceOwner.CleanupConfirmed.Should().BeTrue();
        ids.Count.Should().Be(1);

        var resolution = await Store(source).ResolveRecipientAsync(original, BackgroundJobLifecycleProjectionDeclaration.Create());
        resolution.ThrowIfFailed();
        resolution.CommitAcknowledged.Should().BeTrue();
        resolution.CleanupConfirmed.Should().BeTrue();
        resolution.Value!.Status.Should().Be(committed ? BackgroundJobRecipientResolutionStatus.Accepted
            : BackgroundJobRecipientResolutionStatus.Deferred);
        resolution.Value.Child.Should().Be(committed ? original.Command.ProvisionalChild : null);
        original.Command.Gate.CommitAcknowledged.Should().BeFalse("a durable observation never manufactures the original COMMIT ACK");
        original.Command.AcceptedChild.Should().BeNull();
        ids.Count.Should().Be(1, "history never calls the application identity factory or remints executable entry");
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(committed ? 1 : 0);
        (await ReadAsync<bool>(dispatcher, committed
            ? "SELECT accepted_job_id IS NOT NULL AND accepted_at IS NOT NULL AND ownership_token IS NULL FROM background_job_recipients"
            : "SELECT accepted_job_id IS NULL AND skipped_at IS NULL AND ownership_token IS NULL AND available_after>clock_timestamp() FROM background_job_recipients"))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Confined_recipient_skip_rechecks_current_lifecycle_and_defers_a_restored_route(bool restored)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await SetRecipientTenantStatusAsync(database, "Suspended");
        await using var host = RecipientHost(database, new());
        var original = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
        original.Status.Should().Be(JobRecipientSubmissionStatus.Ineligible);
        original.Command!.Gate.KnownRollback.Should().BeTrue();
        if (restored) await SetRecipientTenantStatusAsync(database, "Active");

        var resolution = await Store(source).ResolveRecipientAsync(original, BackgroundJobLifecycleProjectionDeclaration.Create());
        resolution.ThrowIfFailed();
        resolution.Value!.Status.Should().Be(restored ? BackgroundJobRecipientResolutionStatus.Deferred
            : BackgroundJobRecipientResolutionStatus.Skipped);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        (await ReadAsync<bool>(dispatcher, restored
            ? "SELECT skipped_at IS NULL AND accepted_job_id IS NULL AND ownership_token IS NULL AND available_after>clock_timestamp() FROM background_job_recipients"
            : "SELECT skipped_at IS NOT NULL AND skipped_reason='ineligible' AND accepted_job_id IS NULL AND ownership_token IS NULL FROM background_job_recipients"))
            .Should().BeTrue();
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recipient_resolution_never_changes_a_replaced_or_expired_acquisition(bool expired)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await SetRecipientTenantStatusAsync(database, "Suspended");
        await using var host = RecipientHost(database, new());
        var original = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
        original.Status.Should().Be(JobRecipientSubmissionStatus.Ineligible);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null,
            "UPDATE background_job_recipients SET ownership_until=clock_timestamp()-interval '1 second'");
        var currentToken = expired ? claim.Token : (await ClaimRecipientAsync(source, manifest)).Token;
        if (!expired) currentToken.Should().NotBe(claim.Token, "replacement follows actual expiry and a fresh acknowledged acquisition");

        var resolution = await Store(source).ResolveRecipientAsync(original, BackgroundJobLifecycleProjectionDeclaration.Create());
        resolution.ThrowIfFailed();
        resolution.Value!.Status.Should().Be(BackgroundJobRecipientResolutionStatus.LostClaim);
        (await ReadAsync<bool>(dispatcher,
            "SELECT skipped_at IS NULL AND accepted_job_id IS NULL AND ownership_token=@token FROM background_job_recipients", ("token", currentToken)))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Recipient_history_yields_to_a_real_row_lock_and_observes_the_receipt_only_after_the_barrier()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await using var host = RecipientHost(database, new());
        var original = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
        original.Status.Should().Be(JobRecipientSubmissionStatus.Accepted);
        await using var locker = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await using var transaction = await locker.BeginTransactionAsync();
        await SchemaQueries.ExecuteAsync(locker, transaction, "SELECT id FROM background_job_recipients FOR UPDATE");
        var blocked = await Store(source).ResolveRecipientAsync(original, BackgroundJobLifecycleProjectionDeclaration.Create());
        blocked.ThrowIfFailed();
        blocked.Value.Should().BeNull("SKIP LOCKED is unavailable, never evidence that a child is absent");
        blocked.CleanupConfirmed.Should().BeTrue();
        await transaction.RollbackAsync();
        var observed = await Store(source).ResolveRecipientAsync(original, BackgroundJobLifecycleProjectionDeclaration.Create());
        observed.ThrowIfFailed();
        observed.Value!.Child.Should().Be(original.Command!.AcceptedChild);
    }

    [Fact]
    public async Task A_later_acquisitions_child_receipt_wins_without_attributing_its_ACK_to_the_original_attempt()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        await using var proxy = new CommitResponseDroppingProxy(database.AppConnectionString, dropCommitRequest: true);
        await using var firstHost = RecipientHost(database, new(), proxy.ConnectionString);
        var original = await firstHost.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
        original.Status.Should().Be(JobRecipientSubmissionStatus.Indeterminate);
        await using var dispatcher = await PostgresFixture.OpenAsync(database.OutboxConnectionString);
        await SchemaQueries.ExecuteAsync(dispatcher, null,
            "UPDATE background_job_recipients SET ownership_until=clock_timestamp()-interval '1 second'");
        var replacement = await ClaimRecipientAsync(source, manifest);
        replacement.Token.Should().NotBe(claim.Token);
        await using var secondHost = RecipientHost(database, new());
        var accepted = await secondHost.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(replacement,
            BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
        accepted.Status.Should().Be(JobRecipientSubmissionStatus.Accepted);
        accepted.Command!.AcceptedChild.Should().NotBe(original.Command!.ProvisionalChild);

        var history = await Store(source).ResolveRecipientAsync(original, BackgroundJobLifecycleProjectionDeclaration.Create());
        history.ThrowIfFailed();
        history.Value!.Status.Should().Be(BackgroundJobRecipientResolutionStatus.Accepted);
        history.Value.Child.Should().Be(accepted.Command.AcceptedChild);
        original.Command.AcceptedChild.Should().BeNull();
        original.Command.Gate.CommitAcknowledged.Should().BeFalse();
        (await ReadAsync<long>(dispatcher, "SELECT count(*) FROM background_jobs")).Should().Be(1);
    }

    [Fact]
    public async Task Actual_recipient_scope_cleanup_failure_refuses_a_new_history_operation_even_after_positive_COMMIT_ACK()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = Source(database);
        var manifest = await PrepareClaimRouteAsync(database, Store(source), organization: false);
        var claim = await ClaimRecipientAsync(source, manifest);
        var cleanup = new RecipientCleanupObservation();
        await using var host = RecipientHost(database, new(), configure: services =>
        {
            services.AddSingleton(cleanup);
            services.AddScoped<SubmitBackgroundJobRecipientCommandHandler>();
            services.RemoveAll<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>>();
            services.AddScoped<IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>, ObservedRecipientSubmission>();
            services.AddScoped<FailRecipientScopeCleanup>();
        });
        var original = await host.Services.GetRequiredService<JobExecutor>().ExecuteRecipientAsync(claim,
            BackgroundJobLifecycleProjectionDeclaration.Create(), RecipientGate(), CancellationToken.None);
        cleanup.Disposed.Should().BeTrue();
        original.Status.Should().Be(JobRecipientSubmissionStatus.Accepted);
        original.Command!.Gate.CommitAcknowledged.Should().BeTrue();
        original.CleanupFailed.Should().BeTrue();
        original.RetainedCleanup.Should().NotBeEmpty();
        Func<Task> resolve = async () => await Store(source).ResolveRecipientAsync(original, BackgroundJobLifecycleProjectionDeclaration.Create());
        await resolve.Should().ThrowAsync<InvalidOperationException>().WithMessage("*original completed application cleanup*");
    }

    private static BackgroundJobCommitGate RecipientGate() => BackgroundJobTestRun.Create().OpenBoundary(BackgroundJobCheckpointKind.RecipientSubmission);

    private static async Task SetRecipientTenantStatusAsync(DisposableSchemaDatabase database, string status)
    {
        await using var app = await PostgresFixture.OpenAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await SchemaQueries.AssertApplicationRoleAsync(app, transaction);
        await EducationSchemaSeed.AnnounceAsync(app, transaction, SchemaFixture.TenantA, null);
        await SchemaQueries.ExecuteAsync(app, transaction, "UPDATE tenants SET status=@status WHERE id=@tenant",
            ("status", status), ("tenant", SchemaFixture.TenantA));
        await transaction.CommitAsync();
    }

    private sealed class RecipientIdentityCounter : IGuidFactory
    {
        private int _count;
        internal int Count => Volatile.Read(ref _count);
        public Guid NewUuidV7() { Interlocked.Increment(ref _count); return Guid.CreateVersion7(); }
        public Guid NewUuidV4() => Guid.NewGuid();
    }

    private sealed class RecipientCleanupObservation { internal bool Disposed { get; set; } }
    private sealed class FailRecipientScopeCleanup(RecipientCleanupObservation observation) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            observation.Disposed = true;
            throw new InvalidOperationException("Deliberate actual recipient scope cleanup failure");
        }
    }

    private sealed class ObservedRecipientSubmission(SubmitBackgroundJobRecipientCommandHandler actual, IServiceProvider services)
        : IRequestHandler<SubmitBackgroundJobRecipientCommand, Result<BackgroundJobId>>
    {
        public async Task<Result<BackgroundJobId>> Handle(SubmitBackgroundJobRecipientCommand request, CancellationToken cancellationToken)
        {
            _ = services.GetRequiredService<FailRecipientScopeCleanup>();
            return await actual.Handle(request, cancellationToken);
        }
    }
}
