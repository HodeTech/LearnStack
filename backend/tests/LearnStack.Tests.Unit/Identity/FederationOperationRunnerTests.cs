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
/// dispatches to the real handlers through <see cref="FederationHarness"/>, inside a simulated transaction.
/// </summary>
public sealed class FederationOperationRunnerTests
{
    private static readonly UserId AdminA = FederationHarness.AdminA;
    private static readonly UserId AdminB = FederationHarness.AdminB;
    private static readonly TenantId Tenant = FederationHarness.Tenant;

    [Fact]
    public async Task Configure_runs_Begin_then_the_provider_with_no_transaction_open_then_Complete()
    {
        using var harness = new FederationHarness();
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
        using var harness = new FederationHarness(provider: TimeSpan.FromMilliseconds(50));
        harness.Provider.OnApply = async (_, token) => { await Task.Delay(Timeout.Infinite, token); return null; };
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.Equal(FederationResultKind.Uncertain, run.Value!.Receipt.Outcome);
        Assert.True(harness.Connection!.HasPendingOperation);
        Assert.Equal("complete:Uncertain", harness.Log[^1]);
    }

    [Fact]
    public async Task A_client_abort_during_the_provider_call_still_settles_on_the_detached_Complete()
    {
        using var harness = new FederationHarness();
        using var request = new CancellationTokenSource();
        harness.Provider.OnApply = async (_, token) => { await request.CancelAsync(); await Task.Delay(Timeout.Infinite, token); return null; };
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, request.Token);
        Assert.Equal(FederationResultKind.Uncertain, run.Value!.Receipt.Outcome);
        Assert.Equal("complete:Uncertain", harness.Log[^1]);
    }

    [Fact]
    public async Task A_refused_rename_of_an_Active_connection_is_drift_found_at_the_next_readback_not_a_silently_advertised_name()
    {
        using var harness = await ActiveAsync();
        var before = harness.Connection!.DisplayName;
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Refused);
        var run = await harness.Runner().RenameAsync(harness.Connection.Version, "Renamed", CancellationToken.None);
        Assert.Equal(FederationResultKind.Failed, run.Value!.Receipt.Outcome);
        // The provider still holds the old name, so the row's unconfirmed name must not stay advertised.
        Assert.Equal(FederationConnectionState.Active, harness.Connection.State);
        Assert.NotEqual(before, harness.Connection.DisplayName);
        harness.Provider.OnApply = null;
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationConnectionStatus.Draft, detail.Value!.Connection!.Status);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
        Assert.False(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled, "the corrective write disabled the broker");
        // Recorded once: a further load changes nothing.
        var version = harness.Connection.Version;
        await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(version, harness.Connection.Version);
    }

    [Fact]
    public async Task An_entitlement_lapse_disables_an_Active_broker_at_the_next_readback_and_an_entitled_tenant_is_left_alone()
    {
        using var harness = await ActiveAsync();
        var alias = harness.Connection!.Alias;
        // Dirty control: entitled, the readback changes nothing.
        var entitled = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationConnectionStatus.Active, entitled.Value!.Connection!.Status);
        Assert.True(harness.Provider.Find(alias)!.State.Enabled);
        // ADR-0099 § Lifecycle: the lapse hides the option at once; the broker is disabled by the next readback.
        harness.Entitled = false;
        var lapsed = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationConnectionStatus.Disabled, lapsed.Value!.Connection!.Status);
        Assert.Equal(FederationConnectionState.Disabled, harness.Connection.State);
        Assert.False(harness.Provider.Find(alias)!.State.Enabled, "the broker was disabled through the provider, not only in the row");
        Assert.False(harness.Connection.HasPendingOperation);
    }

    [Fact]
    public async Task A_provider_refusal_settles_as_failed_and_clears_the_pending_operation()
    {
        using var harness = new FederationHarness();
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Refused);
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.Equal(FederationResultKind.Failed, run.Value!.Receipt.Outcome);
        Assert.Equal(FederationFailureReason.ProviderRefused, run.Value.Receipt.Reason);
        Assert.False(harness.Connection!.HasPendingOperation);
    }

    [Fact]
    public async Task A_readback_with_a_mapper_is_drift_and_returns_the_connection_to_Draft()
    {
        using var harness = await ActiveAsync();
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
        using var harness = await ActiveAsync();
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
    public async Task A_lost_test_start_is_resumed_into_a_failure_with_no_attempt_and_the_enabled_broker_is_disabled_at_the_next_readback()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        harness.Provider.OnApply = (write, _) =>
        {
            harness.Provider.Put(write.State.Owner.Alias, new(write.State, true));
            return Task.FromResult<FederationProviderResult?>(FederationProviderResult.Unavailable);
        };
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Uncertain, started.Value!.Receipt.Outcome);
        Assert.True(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled, "the enabling write landed");
        harness.Clock.Advance(FederationConnection.QuietWindow);
        harness.Provider.OnApply = null;
        harness.Actor = AdminB;
        var resumed = await harness.Runner().ResumeAsync(harness.Connection.Version, CancellationToken.None);
        // The browser holds no owner for an attempt, so none may be handed out and no test may open.
        Assert.Equal(FederationResultKind.Failed, resumed.Value!.Receipt.Outcome);
        Assert.Equal(FederationFailureReason.ResubmitRequired, resumed.Value.Receipt.Reason);
        Assert.Null(resumed.Value.TestAttemptId);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
        Assert.Null(harness.Connection.TestAttemptId);
        Assert.False(harness.Connection.HasPendingOperation);
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationConnectionStatus.Draft, detail.Value!.Connection!.Status);
        Assert.False(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled, "a broker left enabled is disabled at the next readback");
    }

    [Fact]
    public async Task A_test_start_returns_a_server_side_attempt_and_its_test_result_is_recorded_once()
    {
        using var harness = new FederationHarness();
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
        // ADR-0099 § Lifecycle and test: a failure disables the broker, through its own provider write and Complete.
        Assert.False(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled);
        Assert.False(harness.Connection.HasPendingOperation);
        Assert.Equal(harness.Connection.DesiredDigest, harness.Connection.LastProviderDigest);
        Assert.Equal(harness.Connection.Version, failed.Value.Receipt.Version);
        Assert.Equal(["begin:RecordFederationTestResultCommand", "provider:read", "provider:apply", "provider:read", "complete:Applied"],
            harness.Log.Skip(harness.Log.LastIndexOf("begin:RecordFederationTestResultCommand")));
    }

    [Fact]
    public async Task An_expired_test_window_is_disabled_by_the_next_detail_readback()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        Assert.True(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled);
        harness.Clock.Advance(FederationConnection.TestWindow);
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationConnectionStatus.Draft, detail.Value!.Connection!.Status);
        Assert.Null(detail.Value.Connection.Pending);
        Assert.False(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled);
        Assert.Contains("complete:Applied", harness.Log);
    }

    [Fact]
    public async Task An_expired_activation_window_is_disabled_by_Activate()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        await harness.Runner().RecordTestResultAsync(harness.Connection.Version, started.Value!.TestAttemptId!.Value, true, CancellationToken.None);
        Assert.Equal(FederationConnectionState.TestPassed, harness.Connection.State);
        harness.Clock.Advance(FederationConnection.ActivationWindow);
        var activated = await harness.Runner().ActivateAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Expired, activated.Value!.Receipt.Outcome);
        Assert.Equal(harness.Connection.Version, activated.Value.Receipt.Version);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
        Assert.False(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled);
    }

    [Fact]
    public async Task A_detail_readback_that_holds_the_connection_state_writes_nothing()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        var version = harness.Connection.Version;
        harness.Log.Clear();
        // Inside the window the enabled broker is the connection's state: no reconcile, no provider write.
        var open = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationProviderObservation.Matches, open.Value!.Provider);
        Assert.Equal(["begin:GetFederationConnectionQuery", "provider:read"], harness.Log);
        Assert.Equal(version, harness.Connection.Version);
    }

    [Fact]
    public async Task A_failed_test_whose_disable_write_finds_the_provider_unavailable_is_disabled_at_the_next_readback()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        var reads = 0;
        // The test result's own readback succeeds; the disable write's starting readback is unavailable.
        harness.Provider.OnRead = (_, _) => Task.FromResult(++reads == 2 ? FederationBrokerReadback.Unavailable : null);
        var failed = await harness.Runner().RecordTestResultAsync(harness.Connection.Version, started.Value!.TestAttemptId!.Value, false,
            CancellationToken.None);
        Assert.Equal(FederationResultKind.Failed, failed.Value!.Receipt.Outcome);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
        Assert.False(harness.Connection.HasPendingOperation);
        Assert.True(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled, "the disable write did not land");
        harness.Provider.OnRead = null;
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationConnectionStatus.Draft, detail.Value!.Connection!.Status);
        Assert.False(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled, "a broker left enabled is disabled at the next readback");
    }

    [Fact]
    public async Task The_detail_read_does_not_read_through_a_missing_connection_or_a_pending_operation()
    {
        using var harness = new FederationHarness();
        // No connection: nothing to read back.
        var none = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Null(none.Value!.Connection);
        Assert.Equal(FederationProviderObservation.NotRead, none.Value.Provider);
        Assert.DoesNotContain("provider:read", harness.Log);
        // A pending operation owns the provider state: the read shows it as it is and never races its write.
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Unavailable);
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.True(harness.Connection!.HasPendingOperation);
        harness.Log.Clear();
        var pending = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.NotNull(pending.Value!.Connection!.Pending);
        Assert.Equal(FederationProviderObservation.NotRead, pending.Value.Provider);
        Assert.Equal(["begin:GetFederationConnectionQuery"], harness.Log);
        // Dirty control: once nothing is pending, the same read does read the provider.
        harness.Provider.OnApply = null;
        harness.Clock.Advance(FederationConnection.QuietWindow);
        Assert.True((await harness.Runner().ResumeAsync(harness.Connection.Version, CancellationToken.None)).IsSuccess);
        harness.Log.Clear();
        var settled = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.NotEqual(FederationProviderObservation.NotRead, settled.Value!.Provider);
        Assert.Contains("provider:read", harness.Log);
    }

    [Fact]
    public async Task A_second_mutation_is_refused_while_an_operation_is_pending_and_Resume_waits_for_the_quiet_window()
    {
        using var harness = new FederationHarness();
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
        using var harness = await ActiveAsync();
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
        using var harness = new FederationHarness();
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
        using var harness = new FederationHarness();
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
        using var harness = new FederationHarness { Entitled = false };
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.Equal("feature_disabled", run.Error!.Code);
        Assert.Equal(0, harness.Egress.Calls);
        Assert.Empty(harness.Log);
    }

    [Fact]
    public async Task A_definitive_detail_readback_drift_is_reconciled_once_and_corrected_with_the_broker_disabled()
    {
        using var harness = await ActiveAsync();
        var alias = harness.Connection!.Alias;
        var broker = harness.Provider.Find(alias)!;
        harness.Provider.Put(alias, broker with { State = broker.State with { DisplayName = "Tampered" } });
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationProviderObservation.Drift, detail.Value!.Provider);
        Assert.Equal(FederationConnectionStatus.Draft, detail.Value.Connection!.Status);
        Assert.Contains("begin:ReconcileFederationConnectionCommand", harness.Log);
        // ADR-0099 § Provider writes: the corrective full-state write with the broker disabled follows the drift.
        Assert.Equal(["provider:read", "provider:apply", "provider:read", "complete:Applied"],
            harness.Log.SkipWhile(entry => entry != "begin:ReconcileFederationConnectionCommand").Skip(1).Take(4));
        Assert.False(harness.Provider.Find(alias)!.State.Enabled);
        Assert.Equal("Acme", harness.Provider.Find(alias)!.State.DisplayName);
        harness.Log.Clear();
        var again = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationProviderObservation.Matches, again.Value!.Provider);
        Assert.Equal(["begin:GetFederationConnectionQuery", "provider:read"], harness.Log);
    }

    /// <summary>
    /// Step 2 review round 2, M-1: a refused Configure of a changed configuration begun from Active leaves the row Draft
    /// with the new configuration and nothing pending, while the provider still holds the old one enabled. The next detail
    /// load disables that broker, keeping the configuration the provider holds.
    /// </summary>
    [Fact]
    public async Task A_refused_changed_Configure_from_Active_is_disabled_by_the_next_detail_readback()
    {
        using var harness = await ActiveAsync();
        var alias = harness.Connection!.Alias;
        var held = harness.Provider.Find(alias)!.State;
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Refused);
        var refused = await harness.Runner().ConfigureAsync(OidcInput() with { ClientId = "client-2" }, harness.Connection.Version,
            CancellationToken.None);
        Assert.Equal(FederationResultKind.Failed, refused.Value!.Receipt.Outcome);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
        Assert.False(harness.Connection.HasPendingOperation);
        Assert.Equal(held, harness.Provider.Find(alias)!.State);
        harness.Provider.OnApply = null;
        harness.Log.Clear();
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationConnectionStatus.Draft, detail.Value!.Connection!.Status);
        Assert.Null(detail.Value.Connection.Pending);
        Assert.Equal(["begin:GetFederationConnectionQuery", "provider:read", "begin:ReconcileFederationConnectionCommand", "provider:read",
            "provider:apply", "provider:read", "complete:Applied", "begin:GetFederationConnectionQuery"], harness.Log);
        AssertDisabledKeepingConfiguration(harness, held);
        harness.Log.Clear();
        var again = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationProviderObservation.Matches, again.Value!.Provider);
        Assert.Equal(["begin:GetFederationConnectionQuery", "provider:read"], harness.Log);
    }

    [Fact]
    public async Task An_uncertain_changed_Configure_from_Active_that_Resume_finds_unapplied_is_disabled_by_the_next_detail_readback()
    {
        using var harness = await ActiveAsync();
        var alias = harness.Connection!.Alias;
        var held = harness.Provider.Find(alias)!.State;
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Unavailable);
        var uncertain = await harness.Runner().ConfigureAsync(OidcInput() with { ClientId = "client-2" }, harness.Connection.Version,
            CancellationToken.None);
        Assert.Equal(FederationResultKind.Uncertain, uncertain.Value!.Receipt.Outcome);
        harness.Provider.OnApply = null;
        harness.Clock.Advance(FederationConnection.QuietWindow);
        var resumed = await harness.Runner().ResumeAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Drift, resumed.Value!.Receipt.Outcome);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
        Assert.False(harness.Connection.HasPendingOperation);
        Assert.Equal(held, harness.Provider.Find(alias)!.State);
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationConnectionStatus.Draft, detail.Value!.Connection!.Status);
        Assert.Null(detail.Value.Connection.Pending);
        AssertDisabledKeepingConfiguration(harness, held);
    }

    [Fact]
    public async Task An_Active_readback_enabled_with_the_connections_own_configuration_writes_nothing()
    {
        using var harness = await ActiveAsync();
        var version = harness.Connection!.Version;
        harness.Log.Clear();
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationProviderObservation.Matches, detail.Value!.Provider);
        Assert.Equal(FederationConnectionStatus.Active, detail.Value.Connection!.Status);
        Assert.Equal(["begin:GetFederationConnectionQuery", "provider:read"], harness.Log);
        Assert.Equal(version, harness.Connection.Version);
        Assert.True(harness.Provider.Find(harness.Connection.Alias)!.State.Enabled);
    }

    [Fact]
    public async Task The_harness_counts_a_provider_call_made_inside_a_transaction_the_runner_would_otherwise_swallow()
    {
        var harness = new FederationHarness();
        var id = Guid.CreateVersion7();
        var owner = new FederationBrokerOwner(Tenant, id, "lsf-" + id.ToString("N"));
        // Planted: the Configure Begin's entitlement check, which runs inside the request, calls the provider.
        harness.Features.IsEnabledAsync(Arg.Any<FeatureKey>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (harness.TransactionOpen) await harness.ObservedProvider.ReadAsync(owner);
            return true;
        });
        Assert.True((await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None)).IsSuccess);
        Assert.Equal(1, harness.TransactionalProviderCalls);
        Assert.Throws<Xunit.Sdk.TrueException>(harness.Dispose);
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
        using var harness = new FederationHarness();
        var runner = new FederationOperationRunner(harness.Sender, provider, egress, harness.Features, harness.RunnerContext, new FederationHarness.Ids(), harness.Clock,
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

    internal static async Task<FederationHarness> ActiveAsync(FederationHarness? existing = null)
    {
        var harness = existing ?? new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        var passed = await harness.Runner().RecordTestResultAsync(harness.Connection.Version, started.Value!.TestAttemptId!.Value, true, CancellationToken.None);
        Assert.Equal(FederationResultKind.Recorded, passed.Value!.Receipt.Outcome);
        var activated = await harness.Runner().ActivateAsync(harness.Connection.Version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Recorded, activated.Value!.Receipt.Outcome);
        return harness;
    }

    /// <summary>
    /// The corrective write disabled the broker and changed nothing else: the configuration the provider held, its display
    /// name and owner are unchanged, and the write carried no secret, so the stored one is kept.
    /// </summary>
    private static void AssertDisabledKeepingConfiguration(FederationHarness harness, FederationBrokerState held)
    {
        var broker = harness.Provider.Find(held.Owner.Alias)!;
        Assert.Equal(held with { Enabled = false }, broker.State);
        Assert.Equal(FederationDigest.Configuration(held.Configuration), FederationDigest.Configuration(broker.State.Configuration));
        Assert.Null(harness.LastWrite!.Secret);
        Assert.False(harness.LastWrite.State.Enabled);
        Assert.Equal(harness.Connection!.DesiredDigest, harness.Connection.LastProviderDigest);
        Assert.NotEqual(FederationDigest.Configuration(held.Configuration), harness.Connection.ConfigurationDigest);
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

    internal static FederationOidcConfiguration OidcConfiguration() => new(InMemoryFederationEgressFetcher.Issuer, "client",
        FederationClientAuthentication.ClientSecretBasic, new Uri(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/auth"),
        new Uri(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/token"),
        new Uri(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/certs"),
        new Uri(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/userinfo"));

    internal static FederationConfigureInput OidcInput()
    {
        Assert.True(FederationClientSecret.TryCreate("s3cret-value", out var secret));
        return new(FederationBrokerProtocol.Oidc, "Acme", FederationSource.DiscoveryUrl, InMemoryFederationEgressFetcher.Discovery, null, null,
            null, null, null, null, [], "client", FederationClientAuthentication.ClientSecretBasic, secret);
    }
}
