using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using MediatR;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// ADR-0099 § Intent-first operations over the in-memory provider: Begin, provider I/O with no transaction open,
/// then Complete, each as its own top-level request; budgets; and every domain path the runner drives. The sender
/// is a domain-backed harness: each request runs the aggregate's own method inside a simulated transaction.
/// </summary>
public sealed class FederationOperationRunnerTests
{
    private static readonly UserId AdminA = UserId.From(Guid.CreateVersion7());
    private static readonly UserId AdminB = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());

    [Fact]
    public async Task Configure_runs_Begin_then_the_provider_with_no_transaction_open_then_Complete()
    {
        var harness = new Harness();
        harness.Provider.BeforeCall = _ => Assert.False(harness.TransactionOpen, "no database transaction is open across provider I/O");
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.True(run.IsSuccess);
        Assert.Equal(FederationResultKind.Applied, run.Value!.Receipt.Outcome);
        Assert.Equal(["begin:BeginConfigureFederationCommand", "provider:apply", "provider:read", "complete:Applied"], harness.Log);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection!.State);
        Assert.False(harness.Connection.HasPendingOperation);
        Assert.Equal(harness.Connection.DesiredDigest, harness.Connection.LastProviderDigest);
        var broker = Assert.IsType<InMemoryFederationProvider.Broker>(harness.Provider.Find(harness.Connection.Alias));
        Assert.False(broker.State.Enabled);
        Assert.Equal("s3cret-value", harness.Provider.LastSecret);
    }

    [Fact]
    public async Task An_over_budget_provider_call_is_uncertain_and_stays_pending()
    {
        var harness = new Harness(provider: TimeSpan.FromMilliseconds(50));
        harness.Provider.OnApply = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; };
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.Equal(FederationResultKind.Uncertain, run.Value!.Receipt.Outcome);
        Assert.True(harness.Connection!.HasPendingOperation);
        Assert.Equal("complete:Uncertain", harness.Log[^1]);
    }

    [Fact]
    public async Task A_client_abort_during_the_provider_call_still_settles_on_the_detached_Complete()
    {
        var harness = new Harness();
        using var request = new CancellationTokenSource();
        harness.Provider.OnApply = async (_, token) => { await request.CancelAsync(); await Task.Delay(Timeout.Infinite, token); return null; };
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, request.Token);
        Assert.Equal(FederationResultKind.Uncertain, run.Value!.Receipt.Outcome);
        Assert.Equal("complete:Uncertain", harness.Log[^1]);
    }

    [Fact]
    public async Task A_provider_refusal_settles_as_failed_and_clears_the_pending_operation()
    {
        var harness = new Harness();
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Refused);
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.Equal(FederationResultKind.Failed, run.Value!.Receipt.Outcome);
        Assert.Equal(FederationFailureReason.ProviderRefused, run.Value.Receipt.Reason);
        Assert.False(harness.Connection!.HasPendingOperation);
    }

    [Fact]
    public async Task A_readback_with_a_mapper_is_drift_and_returns_the_connection_to_Draft()
    {
        var harness = await ActiveAsync();
        harness.Provider.OnRead = (owner, _) =>
        {
            var broker = harness.Provider.Find(owner.Alias)!;
            return Task.FromResult<FederationBrokerReadback?>(FederationBrokerReadback.Present(broker.State, 1, true));
        };
        var run = await harness.Runner().RenameAsync(harness.Connection!.Version, "Renamed", CancellationToken.None);
        Assert.Equal(FederationResultKind.Drift, run.Value!.Receipt.Outcome);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
        Assert.Equal(2, harness.SavesInLastRequest);
    }

    [Fact]
    public async Task Configure_test_activate_and_disable_walk_the_lifecycle_with_the_provider_enabled_only_while_admitted()
    {
        var harness = await ActiveAsync();
        Assert.Equal(FederationConnectionState.Active, harness.Connection!.State);
        Assert.True(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled);
        var disabled = await harness.Runner().DisableAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Applied, disabled.Value!.Receipt.Outcome);
        Assert.Equal(FederationConnectionState.Disabled, harness.Connection.State);
        Assert.False(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled);
        var enabled = await harness.Runner().EnableAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Recorded, enabled.Value!.Receipt.Outcome);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
        var deleted = await harness.Runner().DeleteAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Applied, deleted.Value!.Receipt.Outcome);
        Assert.Equal(FederationConnectionState.Deleted, harness.Connection.State);
        Assert.Null(harness.Provider.Find(harness.Connection.Alias));
    }

    [Fact]
    public async Task A_test_start_returns_a_server_side_attempt_and_its_test_result_is_recorded_once()
    {
        var harness = new Harness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Applied, started.Value!.Receipt.Outcome);
        var attempt = Assert.IsType<Guid>(started.Value.TestAttemptId);
        Assert.Equal(FederationConnectionState.TestOpen, harness.Connection.State);
        var wrong = await harness.Runner().RecordTestResultAsync(harness.Connection.Version, Guid.CreateVersion7(), true, CancellationToken.None);
        Assert.True(wrong.IsFailure);
        var failed = await harness.Runner().RecordTestResultAsync(harness.Connection.Version, attempt, false, CancellationToken.None);
        Assert.Equal(FederationResultKind.Failed, failed.Value!.Receipt.Outcome);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
    }

    [Fact]
    public async Task A_second_mutation_is_refused_while_an_operation_is_pending_and_Resume_waits_for_the_quiet_window()
    {
        var harness = new Harness();
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Unavailable);
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.True(harness.Connection!.HasPendingOperation);
        var rename = await harness.Runner().RenameAsync(harness.Connection.Version, "Other", CancellationToken.None);
        Assert.Equal("concurrency_conflict", rename.Error!.Code);
        var early = await harness.Runner().ResumeAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal("concurrency_conflict", early.Error!.Code);
        harness.Clock.Advance(FederationConnection.QuietWindow);
        harness.Provider.OnApply = null;
        // The write landed late: the readback now equals the stored desired digest, so Resume completes Configure.
        harness.Provider.Put(harness.Connection.Alias, new(harness.LastWrite!.State, true));
        harness.Actor = AdminB;
        var resumed = await harness.Runner().ResumeAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Applied, resumed.Value!.Receipt.Outcome);
        Assert.False(harness.Connection.HasPendingOperation);
        Assert.Equal(AdminB, harness.Connection.UpdatedBy);
    }

    [Fact]
    public async Task An_interrupted_rotation_asks_for_a_resubmit_even_when_the_nonsecret_readback_matches()
    {
        var harness = await ActiveAsync();
        harness.Provider.OnApply = (write, _) =>
        {
            harness.Provider.Put(write.State.Owner.Alias, new(write.State, true));
            return Task.FromResult<FederationProviderResult?>(FederationProviderResult.Unavailable);
        };
        Assert.True(FederationClientSecret.TryCreate("rotated-secret", out var secret));
        var rotated = await harness.Runner().RotateSecretAsync(harness.Connection!.Version, secret!, CancellationToken.None);
        Assert.Equal(FederationResultKind.Uncertain, rotated.Value!.Receipt.Outcome);
        harness.Clock.Advance(FederationConnection.QuietWindow);
        var resumed = await harness.Runner().ResumeAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Failed, resumed.Value!.Receipt.Outcome);
        Assert.Equal(FederationFailureReason.ResubmitRequired, resumed.Value.Receipt.Reason);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
    }

    [Fact]
    public async Task A_revoked_actor_Complete_leaves_the_operation_pending_and_another_administrator_settles_it()
    {
        var harness = new Harness();
        harness.Provider.OnApply = (write, _) =>
        {
            harness.Admins.Remove(AdminA);
            return Task.FromResult<FederationProviderResult?>(null);
        };
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.Equal(FederationResultKind.Pending, run.Value!.Receipt.Outcome);
        Assert.True(harness.Connection!.HasPendingOperation);
        harness.Provider.OnApply = null;
        harness.Clock.Advance(FederationConnection.QuietWindow);
        harness.Actor = AdminB;
        var resumed = await harness.Runner().ResumeAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Applied, resumed.Value!.Receipt.Outcome);
    }

    [Fact]
    public async Task A_superseded_operation_identifier_changes_nothing()
    {
        var harness = new Harness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var version = harness.Connection!.Version;
        var stale = await harness.Sender.Send(new CompleteFederationOperationCommand(harness.Connection.Id.Value, Guid.CreateVersion7(),
            FederationApplyOutcome.Applied, harness.Connection.DesiredDigest, null, null));
        Assert.Equal(FederationResultKind.Superseded, stale.Value!.Outcome);
        Assert.Equal(version, harness.Connection.Version);
    }

    [Fact]
    public async Task An_unentitled_protocol_is_refused_before_egress_or_any_Begin()
    {
        var harness = new Harness { Entitled = false };
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.Equal("feature_disabled", run.Error!.Code);
        Assert.Equal(0, harness.Egress.Calls);
        Assert.Empty(harness.Log);
    }

    [Fact]
    public async Task A_definitive_detail_readback_drift_is_reconciled_once()
    {
        var harness = await ActiveAsync();
        var alias = harness.Connection!.Alias;
        var broker = harness.Provider.Find(alias)!;
        harness.Provider.Put(alias, broker with { State = broker.State with { DisplayName = "Tampered" } });
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationProviderObservation.Drift, detail.Value!.Provider);
        Assert.Equal(FederationConnectionStatus.Draft, detail.Value.Connection!.Status);
        Assert.Contains("begin:ReconcileFederationConnectionCommand", harness.Log);
        harness.Log.Clear();
        var again = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationProviderObservation.Drift, again.Value!.Provider);
        Assert.DoesNotContain("begin:ReconcileFederationConnectionCommand", harness.Log);
    }

    [Fact]
    public async Task The_fail_closed_defaults_report_unavailable_and_Configure_writes_nothing()
    {
        var id = Guid.CreateVersion7();
        var owner = new FederationBrokerOwner(Tenant, id, "lsf-" + id.ToString("N"));
        var provider = new UnconfiguredFederationProvider();
        Assert.Equal(FederationProviderStatus.Unavailable, (await provider.DeleteAsync(owner)).Status);
        Assert.Equal(FederationReadbackStatus.Unavailable, (await provider.ReadAsync(owner)).Status);
        var egress = new UnconfiguredFederationEgressFetcher();
        Assert.Equal(FederationEgressStatus.Unavailable, (await egress.FetchOidcDiscoveryAsync(InMemoryFederationEgressFetcher.Discovery)).Status);
        Assert.Equal(FederationEgressStatus.Unavailable, (await egress.CheckEndpointAsync(InMemoryFederationEgressFetcher.Discovery)).Status);
        var harness = new Harness();
        var runner = new FederationOperationRunner(harness.Sender, provider, egress, harness.Features, harness.Context, new Ids(), harness.Clock,
            FederationOperationBudgets.Default);
        var run = await runner.ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.Equal("dependency_unavailable", run.Error!.Code);
        Assert.Null(harness.Connection);
    }

    [Fact]
    public void The_budgets_are_the_ADR_values()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), FederationOperationBudgets.Default.Egress);
        Assert.Equal(TimeSpan.FromSeconds(1), FederationOperationBudgets.Default.DatabaseStep);
        Assert.Equal(TimeSpan.FromSeconds(2.5), FederationOperationBudgets.Default.Provider);
        Assert.Equal(TimeSpan.FromSeconds(1.5), FederationOperationBudgets.Default.Complete);
    }

    [Fact]
    public void The_tombstone_filter_never_returns_a_Deleted_connection_as_live()
    {
        var at = new DateTimeOffset(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        var tombstone = Draft(at);
        var operation = FederationOperationId.From(Guid.CreateVersion7());
        Assert.True(tombstone.Complete(tombstone.PendingOperationId!.Value, FederationOperationOutcome.Applied, tombstone.PendingDesiredDigest,
            null, at, AdminA).IsSuccess);
        Assert.True(tombstone.BeginDelete(FederationDigest.Absent, operation, at, AdminA).IsSuccess);
        Assert.True(tombstone.Complete(operation, FederationOperationOutcome.Applied, FederationDigest.Absent, null, at, AdminA).IsSuccess);
        Assert.Equal(FederationConnectionState.Deleted, tombstone.State);
        var live = Draft(at);
        Assert.Equal([live], new[] { tombstone, live }.AsQueryable().Live().ToArray());
        Assert.Empty(new[] { tombstone }.AsQueryable().Live());
    }

    [Fact]
    public void Readback_digests_equal_the_desired_digest_only_for_an_owned_conforming_mapper_free_broker()
    {
        var id = Guid.CreateVersion7();
        var owner = new FederationBrokerOwner(Tenant, id, "lsf-" + id.ToString("N"));
        var state = new FederationBrokerState(owner, "Acme", true, OidcConfiguration());
        var desired = FederationDigest.Desired(state);
        Assert.Equal(desired, FederationDigest.Readback(owner, FederationBrokerReadback.Present(state, 0, true)));
        Assert.NotEqual(desired, FederationDigest.Readback(owner, FederationBrokerReadback.Present(state, 1, true)));
        Assert.NotEqual(desired, FederationDigest.Readback(owner, FederationBrokerReadback.Present(state, 0, false)));
        Assert.NotEqual(desired, FederationDigest.Readback(owner, FederationBrokerReadback.Present(state with { Enabled = false }, 0, true)));
        Assert.Equal(FederationDigest.Absent, FederationDigest.Readback(owner, FederationBrokerReadback.Absent));
        Assert.Null(FederationDigest.Readback(owner, FederationBrokerReadback.Unavailable));
        Assert.Null(FederationDigest.Readback(owner, FederationBrokerReadback.Foreign));
    }

    [Theory]
    [InlineData("")]
    [InlineData("**********")]
    [InlineData("${vault.secret}")]
    public void A_refused_client_secret_never_becomes_a_value(string value) =>
        Assert.False(FederationClientSecret.TryCreate(value, out _));

    [Fact]
    public void A_client_secret_renders_and_serializes_as_nothing()
    {
        Assert.True(FederationClientSecret.TryCreate(new string('x', 1024), out var secret));
        Assert.Equal("[redacted]", secret!.ToString());
        Assert.Throws<System.Text.Json.JsonException>(() => System.Text.Json.JsonSerializer.Serialize(secret));
        Assert.False(FederationClientSecret.TryCreate(new string('x', 1025), out _));
        Assert.DoesNotContain("s3cret", OidcInput().ToString(), StringComparison.Ordinal);
    }

    private static async Task<Harness> ActiveAsync()
    {
        var harness = new Harness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        var passed = await harness.Runner().RecordTestResultAsync(harness.Connection.Version, started.Value!.TestAttemptId!.Value, true, CancellationToken.None);
        Assert.Equal(FederationResultKind.Recorded, passed.Value!.Receipt.Outcome);
        var activated = await harness.Runner().ActivateAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Recorded, activated.Value!.Receipt.Outcome);
        return harness;
    }

    private static FederationConnection Draft(DateTimeOffset at)
    {
        var id = FederationConnectionId.From(Guid.CreateVersion7());
        var digest = FederationDigest.Configuration(OidcConfiguration());
        return FederationConnection.Create(id, Tenant, FederationProtocol.Oidc, "Acme", new FederationConfiguration(FederationSourceKind.DiscoveryUrl,
            InMemoryFederationEgressFetcher.Issuer, "client", digest,
            FederationDigest.Desired(FederationConnection.DeriveAlias(id), FederationBrokerProtocol.Oidc, digest, "Acme", false)),
            FederationOperationId.From(Guid.CreateVersion7()), at, AdminA).Value!;
    }

    private static FederationOidcConfiguration OidcConfiguration() => new(InMemoryFederationEgressFetcher.Issuer, "client",
        FederationClientAuthentication.ClientSecretBasic, new Uri(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/auth"),
        new Uri(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/token"),
        new Uri(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/certs"),
        new Uri(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/userinfo"));

    private static FederationConfigureInput OidcInput()
    {
        Assert.True(FederationClientSecret.TryCreate("s3cret-value", out var secret));
        return new(FederationBrokerProtocol.Oidc, "Acme", FederationSource.DiscoveryUrl, InMemoryFederationEgressFetcher.Discovery, null, null,
            null, null, null, null, [], "client", FederationClientAuthentication.ClientSecretBasic, secret);
    }

    private sealed class Ids : IGuidFactory
    {
        public Guid NewUuidV7() => Guid.CreateVersion7();
        public Guid NewUuidV4() => Guid.NewGuid();
    }

    private sealed class MovableClock : IClock
    {
        private DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        public DateTimeOffset UtcNow => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class Context(Harness harness) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId => Tenant;
        public OrganizationId? OrganizationId => null;
        public UserId? UserId => harness.Actor;
        public string? CorrelationId => "federation-unit";
        public string? ModuleName => "identity";
    }

    /// <summary>
    /// A domain-backed sender: each request opens a simulated transaction, authorizes the current actor, runs the
    /// aggregate's own method and "saves" each step. It mirrors the handlers' refusals, not their storage.
    /// </summary>
    private sealed class Harness
    {
        internal Harness(TimeSpan? provider = null)
        {
            Clock = new MovableClock();
            Context = new Context(this);
            Admins = [AdminA, AdminB];
            Features = Substitute.For<IFeatureFlags>();
            Features.IsEnabledAsync(Arg.Any<FeatureKey>(), Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult(Entitled));
            Sender = Substitute.For<ISender>();
            Sender.Send(Arg.Any<IRequest<Result<FederationPendingOperation>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Run<FederationPendingOperation>(call.ArgAt<object>(0))));
            Sender.Send(Arg.Any<IRequest<Result<FederationOperationReceipt>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Run<FederationOperationReceipt>(call.ArgAt<object>(0))));
            Sender.Send(Arg.Any<IRequest<Result<FederationConnectionRead>>>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Run<FederationConnectionRead>(call.ArgAt<object>(0))));
            ProviderBudget = provider ?? TimeSpan.FromSeconds(5);
        }

        internal InMemoryFederationProvider Provider { get; } = new();
        internal InMemoryFederationEgressFetcher Egress { get; } = new();
        internal IFeatureFlags Features { get; }
        internal ISender Sender { get; }
        internal MovableClock Clock { get; }
        internal ITenantContext Context { get; }
        internal HashSet<UserId> Admins { get; }
        internal UserId Actor { get; set; } = AdminA;
        internal bool Entitled { get; set; } = true;
        internal bool TransactionOpen { get; private set; }
        internal FederationConnection? Connection { get; private set; }
        internal List<string> Log { get; } = [];
        internal int SavesInLastRequest { get; private set; }
        internal FederationBrokerWrite? LastWrite { get; set; }
        private TimeSpan ProviderBudget { get; }

        internal FederationOperationRunner Runner()
        {
            return new FederationOperationRunner(Sender, new Unwrapping(this), Egress, Features, Context, new Ids(), Clock,
                FederationOperationBudgets.Default with { Provider = ProviderBudget, Complete = TimeSpan.FromSeconds(5), DatabaseStep = TimeSpan.FromSeconds(5) });
        }

        private Result<T> Run<T>(object request)
        {
            TransactionOpen = true;
            SavesInLastRequest = 0;
            try
            {
                Log.Add(request is CompleteFederationOperationCommand completing ? "complete:" + completing.Outcome : "begin:" + request.GetType().Name);
                if (!Admins.Contains(Actor)) return Fail<T>("lockey_forbidden");
                var now = Clock.UtcNow;
                object? result = request switch
                {
                    BeginConfigureFederationCommand configure => Configure(configure, now),
                    BeginRotateFederationSecretCommand rotate => Begin(rotate.ExpectedVersion, c => c.BeginConfigure(FederationOperationKind.RotateSecret,
                        new FederationConfiguration(c.SourceKind, c.IssuerOrEntityId, c.ClientId, c.ConfigurationDigest, Desired(c, c.DisplayName, false)),
                        NewOperation(), now, Actor)),
                    BeginRenameFederationCommand rename => Begin(rename.ExpectedVersion, c => c.BeginRename(rename.DisplayName,
                        Desired(c, rename.DisplayName, c.State is FederationConnectionState.TestOpen or FederationConnectionState.TestPassed or FederationConnectionState.Active),
                        NewOperation(), now, Actor)),
                    BeginFederationTestStartCommand start => Begin(start.ExpectedVersion, c => c.BeginTestStart(Desired(c, c.DisplayName, true), NewOperation(), now, Actor)),
                    BeginDisableFederationCommand disable => Begin(disable.ExpectedVersion, c => c.BeginDisable(Desired(c, c.DisplayName, false), NewOperation(), now, Actor)),
                    BeginDeleteFederationCommand delete => Begin(delete.ExpectedVersion, c => c.BeginDelete(FederationDigest.Absent, NewOperation(), now, Actor)),
                    ResumeFederationOperationCommand resume => Connection is null || Connection.Version != resume.ExpectedVersion ? Fail<T>("lockey_concurrency_conflict")
                        : !Connection.CanResume(now) ? Fail<T>("lockey_concurrency_conflict") : Result.Ok(Pending(Connection)),
                    CompleteFederationOperationCommand complete => Complete(complete, now),
                    EnableFederationCommand enable => Record(enable.ExpectedVersion, c => c.Enable(now, Actor)),
                    ActivateFederationCommand activate => Activate(activate, now),
                    RecordFederationTestResultCommand record => Record(record.ExpectedVersion, c =>
                        c.RecordTestResult(FederationTestAttemptId.From(record.TestAttemptId), record.Passed, now, Actor), record.Passed ? FederationResultKind.Recorded : FederationResultKind.Failed),
                    ReconcileFederationConnectionCommand reconcile => Record(reconcile.ExpectedVersion, c => c.RecordDrift(reconcile.ProviderDigest!, now, Actor), FederationResultKind.Drift),
                    GetFederationConnectionQuery => Result.Ok(new FederationConnectionRead(Connection is null || Connection.State == FederationConnectionState.Deleted ? null : View(Connection))),
                    _ => throw new InvalidOperationException(request.GetType().Name),
                };
                return (Result<T>)result!;
            }
            finally { TransactionOpen = false; }
        }

        private Result<FederationPendingOperation> Configure(BeginConfigureFederationCommand request, DateTimeOffset now)
        {
            if (Connection is null)
            {
                var id = FederationConnectionId.From(Guid.CreateVersion7());
                var desired = FederationDigest.Desired(FederationConnection.DeriveAlias(id), request.Protocol, request.ConfigurationDigest, request.DisplayName!, false);
                var created = FederationConnection.Create(id, Tenant, FederationProtocol.Oidc, request.DisplayName!, new FederationConfiguration(
                    (FederationSourceKind)(int)request.Source, request.IssuerOrEntityId, request.ClientId, request.ConfigurationDigest, desired), NewOperation(), now, Actor);
                if (created.IsFailure) return Fail<FederationPendingOperation>("lockey_business_rule_violation");
                Connection = created.Value;
                SavesInLastRequest++;
                return Result.Ok(Pending(Connection!));
            }
            return Begin(request.ExpectedVersion ?? -1, c => c.BeginConfigure(FederationOperationKind.Configure, new FederationConfiguration(
                (FederationSourceKind)(int)request.Source, request.IssuerOrEntityId, request.ClientId, request.ConfigurationDigest,
                FederationDigest.Desired(c.Alias, request.Protocol, request.ConfigurationDigest, c.DisplayName, false)), NewOperation(), now, Actor));
        }

        private Result<FederationPendingOperation> Begin(long expected, Func<FederationConnection, Result<None>> act)
        {
            if (Connection is null) return Fail<FederationPendingOperation>("lockey_not_found");
            if (Connection.Version != expected) return Fail<FederationPendingOperation>("lockey_concurrency_conflict");
            var begun = act(Connection);
            if (begun.IsFailure) return Fail<FederationPendingOperation>(Connection.HasPendingOperation ? "lockey_concurrency_conflict" : "lockey_business_rule_violation");
            SavesInLastRequest++;
            return Result.Ok(Pending(Connection));
        }

        private Result<FederationOperationReceipt> Complete(CompleteFederationOperationCommand request, DateTimeOffset now)
        {
            if (Connection is null || Connection.PendingOperationId?.Value != request.OperationId)
                return Result.Ok(new FederationOperationReceipt(request.ConnectionId, Connection?.Version ?? 0, FederationResultKind.Superseded));
            var outcome = (FederationOperationOutcome)(int)request.Outcome;
            var attempt = request.TestAttemptId is { } value ? FederationTestAttemptId.From(value) : (FederationTestAttemptId?)null;
            var completed = Connection.Complete(FederationOperationId.From(request.OperationId), outcome, request.ProviderDigest, attempt, now, Actor);
            if (completed.IsFailure) return Fail<FederationOperationReceipt>("lockey_business_rule_violation");
            SavesInLastRequest++;
            if (outcome == FederationOperationOutcome.Failed && request.ProviderDigest is { } observed && Connection.State != FederationConnectionState.Deleted
                && observed != Connection.DesiredDigest)
            {
                Assert.True(Connection.RecordDrift(observed, now, Actor).IsSuccess);
                SavesInLastRequest++;
                return Result.Ok(new FederationOperationReceipt(Connection.Id.Value, Connection.Version, FederationResultKind.Drift, FederationFailureReason.Drift));
            }
            return Result.Ok(new FederationOperationReceipt(Connection.Id.Value, Connection.Version, (FederationResultKind)(int)request.Outcome,
                outcome == FederationOperationOutcome.Failed ? request.Reason : null));
        }

        private Result<FederationOperationReceipt> Activate(ActivateFederationCommand request, DateTimeOffset now)
        {
            if (Connection is null || Connection.Version != request.ExpectedVersion) return Fail<FederationOperationReceipt>("lockey_concurrency_conflict");
            if (request.ProviderDigest != Connection.DesiredDigest)
                return Record(request.ExpectedVersion, c => c.RecordDrift(request.ProviderDigest, now, Actor), FederationResultKind.Drift);
            return Record(request.ExpectedVersion, c => c.Activate(request.ProviderDigest, now, Actor));
        }

        private Result<FederationOperationReceipt> Record(long expected, Func<FederationConnection, Result<None>> act,
            FederationResultKind kind = FederationResultKind.Recorded)
        {
            if (Connection is null) return Fail<FederationOperationReceipt>("lockey_not_found");
            if (Connection.Version != expected) return Fail<FederationOperationReceipt>("lockey_concurrency_conflict");
            if (act(Connection).IsFailure) return Fail<FederationOperationReceipt>("lockey_business_rule_violation");
            SavesInLastRequest++;
            return Result.Ok(new FederationOperationReceipt(Connection.Id.Value, Connection.Version, kind));
        }

        private static FederationConnectionView View(FederationConnection c) => new(c.Id.Value, FederationBrokerProtocol.Oidc, c.Alias, c.DisplayName,
            c.IssuerOrEntityId, (FederationConnectionStatus)(int)c.State, (FederationSource)(int)c.SourceKind, c.ClientId, c.ConfigurationRevision,
            c.ConfigurationDigest, c.DesiredDigest, c.LastProviderDigest, c.PendingOperationKind is { } kind
                ? new FederationPendingView((FederationPendingKind)(int)kind, c.PendingStartedAt!.Value, c.PendingQuietUntil!.Value) : null,
            c.TestExpiresAt, c.Version);

        private static FederationPendingOperation Pending(FederationConnection c) => new(c.Id.Value, c.PendingOperationId!.Value.Value,
            (FederationPendingKind)(int)c.PendingOperationKind!.Value, FederationBrokerProtocol.Oidc, c.Alias, c.DisplayName,
            c.State is FederationConnectionState.TestOpen or FederationConnectionState.TestPassed or FederationConnectionState.Active,
            c.ConfigurationDigest, c.PendingDesiredDigest!, c.Version);

        private static string Desired(FederationConnection c, string name, bool enabled) =>
            FederationDigest.Desired(c.Alias, FederationBrokerProtocol.Oidc, c.ConfigurationDigest, name, enabled);

        private static FederationOperationId NewOperation() => FederationOperationId.From(Guid.CreateVersion7());

        private static Result<T> Fail<T>(string key) => Result<T>.Fail(new Error(LocalizedMessage.Of(key)));

        /// <summary>Records the provider calls in the harness log and proves no transaction is open across them.</summary>
        private sealed class Unwrapping(Harness harness) : IFederationProvider
        {
            public Task<FederationProviderResult> ApplyAsync(FederationBrokerWrite write, CancellationToken cancellationToken = default)
            { Observe("apply"); harness.LastWrite = write; return harness.Provider.ApplyAsync(write, cancellationToken); }
            public Task<FederationBrokerReadback> ReadAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
            { Observe("read"); return harness.Provider.ReadAsync(owner, cancellationToken); }
            public Task<FederationProviderResult> DeleteAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
            { Observe("delete"); return harness.Provider.DeleteAsync(owner, cancellationToken); }
            private void Observe(string call)
            {
                Assert.False(harness.TransactionOpen, "no database transaction is open across provider I/O");
                harness.Log.Add("provider:" + call);
            }
        }
    }
}
