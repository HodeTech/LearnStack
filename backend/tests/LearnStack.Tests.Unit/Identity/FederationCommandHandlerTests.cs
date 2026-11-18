using System.Collections.Immutable;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identity;
using LearnStack.Tests.Fixtures;
using Xunit;
using static LearnStack.Tests.Unit.Identity.FederationOperationRunnerTests;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// The production federation handlers, each request sent straight to its real handler over a stub store (Step 2 review
/// C-2), and the review's corrections: a fresh secret for a changed OIDC configuration (S-A), provider faults settled as
/// uncertain (S-C) and the disabling write after a failed or expired test (C-1).
/// </summary>
public sealed class FederationCommandHandlerTests
{
    private static readonly Uri ChangedToken = new(InMemoryFederationEgressFetcher.Issuer + "/protocol/openid-connect/token-v2");

    [Fact]
    public async Task Rotate_secret_begins_a_Configure_kind_write_into_Draft_and_refuses_SAML_unentitled_and_stale()
    {
        using var harness = await ActiveAsync();
        var connection = harness.Connection!;
        var revision = connection.ConfigurationRevision;
        harness.Entitled = false;
        Assert.Equal("feature_disabled", (await harness.SendAsync(new BeginRotateFederationSecretCommand(connection.Version))).Error!.Code);
        harness.Entitled = true;
        Assert.Equal("concurrency_conflict", (await harness.SendAsync(new BeginRotateFederationSecretCommand(connection.Version - 1))).Error!.Code);
        Assert.Equal(FederationConnectionState.Active, connection.State);
        var begun = await harness.SendAsync(new BeginRotateFederationSecretCommand(connection.Version));
        Assert.Equal(FederationPendingKind.RotateSecret, begun.Value!.Kind);
        Assert.False(begun.Value.Enabled);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        Assert.Equal(revision + 1, connection.ConfigurationRevision);

        using var saml = new FederationHarness();
        Assert.True((await saml.Runner().ConfigureAsync(SamlInput(), null, CancellationToken.None)).IsSuccess);
        var version = saml.Connection!.Version;
        Assert.Equal("business_rule_violation", (await saml.SendAsync(new BeginRotateFederationSecretCommand(version))).Error!.Code);
        Assert.Equal(version, saml.Connection.Version);
    }

    [Fact]
    public async Task Delete_stops_admission_first_and_refuses_stale_and_pending()
    {
        using var harness = await ActiveAsync();
        var connection = harness.Connection!;
        Assert.Equal("concurrency_conflict", (await harness.SendAsync(new BeginDeleteFederationCommand(connection.Version + 1))).Error!.Code);
        var begun = await harness.SendAsync(new BeginDeleteFederationCommand(connection.Version));
        Assert.Equal(FederationPendingKind.Delete, begun.Value!.Kind);
        Assert.Equal(FederationDigest.Absent, begun.Value.DesiredDigest);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        Assert.Equal("concurrency_conflict", (await harness.SendAsync(new BeginDeleteFederationCommand(connection.Version))).Error!.Code);
        var deleted = await harness.SendAsync(new CompleteFederationOperationCommand(connection.Id.Value, begun.Value.OperationId,
            FederationApplyOutcome.Applied, FederationDigest.Absent, null, null));
        Assert.Equal(FederationResultKind.Applied, deleted.Value!.Outcome);
        Assert.Equal(FederationConnectionState.Deleted, connection.State);
        Assert.Equal("not_found", (await harness.SendAsync(new BeginDeleteFederationCommand(connection.Version))).Error!.Code);
    }

    [Fact]
    public async Task Enable_moves_Disabled_to_Draft_only()
    {
        using var harness = await ActiveAsync();
        var connection = harness.Connection!;
        Assert.Equal("business_rule_violation", (await harness.SendAsync(new EnableFederationCommand(connection.Version))).Error!.Code);
        Assert.True((await harness.Runner().DisableAsync(connection.Version, CancellationToken.None)).IsSuccess);
        Assert.Equal(FederationConnectionState.Disabled, connection.State);
        Assert.Equal("concurrency_conflict", (await harness.SendAsync(new EnableFederationCommand(connection.Version - 1))).Error!.Code);
        var enabled = await harness.SendAsync(new EnableFederationCommand(connection.Version));
        Assert.Equal(FederationResultKind.Recorded, enabled.Value!.Outcome);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        Assert.Equal(1, harness.SavesInLastRequest);
    }

    [Fact]
    public async Task Rename_keeps_the_state_and_its_enabled_value_and_an_expired_window_renames_disabled()
    {
        using var harness = await ActiveAsync();
        var connection = harness.Connection!;
        Assert.Equal("validation_failed", (await harness.SendAsync(new BeginRenameFederationCommand(connection.Version, " "))).Error!.Code);
        var active = await harness.SendAsync(new BeginRenameFederationCommand(connection.Version, "Renamed"));
        Assert.True(active.Value!.Enabled);
        Assert.Equal(FederationConnectionState.Active, connection.State);
        Assert.Equal("Renamed", connection.DisplayName);

        using var testing = new FederationHarness();
        await testing.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        await testing.Runner().StartTestAsync(testing.Connection!.Version, CancellationToken.None);
        testing.Clock.Advance(FederationConnection.TestWindow);
        var expired = await testing.SendAsync(new BeginRenameFederationCommand(testing.Connection.Version, "Later"));
        Assert.False(expired.Value!.Enabled, "the expiry runs first, so the rename writes the broker disabled");
        Assert.Equal(FederationConnectionState.Draft, testing.Connection.State);
        Assert.Equal(2, testing.SavesInLastRequest);
    }

    [Fact]
    public async Task Activate_is_entitlement_gated_records_drift_and_expiry_and_begins_the_disabling_write_on_expiry()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        await harness.Runner().RecordTestResultAsync(harness.Connection.Version, started.Value!.TestAttemptId!.Value, true, CancellationToken.None);
        var connection = harness.Connection;
        harness.Entitled = false;
        Assert.Equal("feature_disabled", (await harness.SendAsync(new ActivateFederationCommand(connection.Version, connection.DesiredDigest))).Error!.Code);
        harness.Entitled = true;
        Assert.Equal("concurrency_conflict", (await harness.SendAsync(new ActivateFederationCommand(connection.Version + 1, connection.DesiredDigest))).Error!.Code);
        harness.Clock.Advance(FederationConnection.ActivationWindow);
        var expired = await harness.SendAsync(new ActivateFederationCommand(connection.Version, connection.DesiredDigest));
        Assert.Equal(FederationResultKind.Expired, expired.Value!.Outcome);
        var corrective = Assert.IsType<FederationPendingOperation>(expired.Value.Corrective);
        Assert.Equal(FederationPendingKind.Rename, corrective.Kind);
        Assert.False(corrective.Enabled);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        Assert.Equal(2, harness.SavesInLastRequest);
    }

    [Fact]
    public async Task Activate_with_a_new_drift_digest_records_drift_instead()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        await harness.Runner().RecordTestResultAsync(harness.Connection.Version, started.Value!.TestAttemptId!.Value, true, CancellationToken.None);
        var drift = new string('a', 64);
        var result = await harness.SendAsync(new ActivateFederationCommand(harness.Connection.Version, drift));
        Assert.Equal(FederationResultKind.Drift, result.Value!.Outcome);
        Assert.Equal(FederationConnectionState.Draft, harness.Connection.State);
        Assert.Equal(drift, harness.Connection.LastProviderDigest);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_test_result_without_a_readback_is_dependency_unavailable_even_when_it_reports_a_failure(bool passed)
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        var version = harness.Connection.Version;
        var result = await harness.SendAsync(new RecordFederationTestResultCommand(version, started.Value!.TestAttemptId!.Value, passed, null));
        Assert.Equal("dependency_unavailable", result.Error!.Code);
        Assert.Equal(0, harness.SavesInLastRequest);
        Assert.Equal(FederationConnectionState.TestOpen, harness.Connection.State);
        Assert.Equal(version, harness.Connection.Version);
    }

    [Fact]
    public async Task A_failed_test_result_records_Draft_and_begins_the_disabling_write_in_the_same_request()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var started = await harness.Runner().StartTestAsync(harness.Connection!.Version, CancellationToken.None);
        var connection = harness.Connection;
        var failed = await harness.SendAsync(new RecordFederationTestResultCommand(connection.Version, started.Value!.TestAttemptId!.Value, false,
            connection.DesiredDigest));
        Assert.Equal(FederationResultKind.Failed, failed.Value!.Outcome);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        var corrective = Assert.IsType<FederationPendingOperation>(failed.Value.Corrective);
        Assert.False(corrective.Enabled);
        Assert.Equal(connection.PendingDesiredDigest, corrective.DesiredDigest);
        Assert.Equal(2, harness.SavesInLastRequest);
    }

    [Fact]
    public async Task Reconcile_refuses_a_pending_or_unchanged_connection_and_records_expiry_and_drift_with_a_disabling_write()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var connection = harness.Connection!;
        Assert.Equal("business_rule_violation",
            (await harness.SendAsync(new ReconcileFederationConnectionCommand(connection.Version, connection.DesiredDigest))).Error!.Code);
        Assert.Equal(0, harness.SavesInLastRequest);
        var drift = new string('b', 64);
        var drifted = await harness.SendAsync(new ReconcileFederationConnectionCommand(connection.Version, drift));
        Assert.Equal(FederationResultKind.Drift, drifted.Value!.Outcome);
        Assert.NotNull(drifted.Value.Corrective);
        Assert.True(connection.HasPendingOperation);
        Assert.Equal("concurrency_conflict", (await harness.SendAsync(new ReconcileFederationConnectionCommand(connection.Version, drift))).Error!.Code);

        using var testing = new FederationHarness();
        await testing.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        await testing.Runner().StartTestAsync(testing.Connection!.Version, CancellationToken.None);
        testing.Clock.Advance(FederationConnection.TestWindow);
        var expired = await testing.SendAsync(new ReconcileFederationConnectionCommand(testing.Connection.Version, testing.Connection.DesiredDigest));
        Assert.Equal(FederationResultKind.Expired, expired.Value!.Outcome);
        Assert.False(expired.Value.Corrective!.Enabled);
        Assert.Equal(FederationConnectionState.Draft, testing.Connection.State);
    }

    [Fact]
    public async Task A_refused_Complete_changes_nothing_and_a_superseded_one_reports_it()
    {
        using var harness = new FederationHarness();
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Unavailable);
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var connection = harness.Connection!;
        var operation = connection.PendingOperationId!.Value.Value;
        var version = connection.Version;
        // Applied must carry exactly the desired digest; anything else is refused and the operation stays pending.
        var refused = await harness.SendAsync(new CompleteFederationOperationCommand(connection.Id.Value, operation, FederationApplyOutcome.Applied,
            new string('c', 64), null, null));
        Assert.Equal("business_rule_violation", refused.Error!.Code);
        Assert.Equal(version, connection.Version);
        Assert.True(connection.HasPendingOperation);
        harness.Actor = FederationHarness.AdminB;
        harness.Admins.Remove(FederationHarness.AdminB);
        Assert.Equal("forbidden", (await harness.SendAsync(new CompleteFederationOperationCommand(connection.Id.Value, operation,
            FederationApplyOutcome.Applied, connection.PendingDesiredDigest, null, null))).Error!.Code);
        Assert.True(connection.HasPendingOperation);
        harness.Actor = FederationHarness.AdminA;
        var superseded = await harness.SendAsync(new CompleteFederationOperationCommand(connection.Id.Value, Guid.CreateVersion7(),
            FederationApplyOutcome.Applied, connection.PendingDesiredDigest, null, null));
        Assert.Equal(FederationResultKind.Superseded, superseded.Value!.Outcome);
        Assert.Equal(version, connection.Version);
    }

    [Fact]
    public async Task A_changed_OIDC_configuration_without_a_fresh_secret_is_refused_before_any_provider_call()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var connection = harness.Connection!;
        var version = connection.Version;
        harness.Log.Clear();
        var refused = await harness.Runner().ConfigureAsync(ManualInput(ChangedToken, secret: null), version, CancellationToken.None);
        Assert.Equal("validation_failed", refused.Error!.Code);
        Assert.Equal(["begin:BeginConfigureFederationCommand"], harness.Log);
        Assert.Equal(version, connection.Version);
        Assert.Equal(1, harness.Provider.SecretWrites);
        // The same change with a fresh secret is a Configure.
        var changed = await harness.Runner().ConfigureAsync(ManualInput(ChangedToken, secret: "rotated-s3cret"), version, CancellationToken.None);
        Assert.Equal(FederationResultKind.Applied, changed.Value!.Receipt.Outcome);
        Assert.Equal("rotated-s3cret", harness.Provider.LastSecret);
    }

    [Fact]
    public async Task An_unchanged_OIDC_configuration_without_a_secret_keeps_the_stored_secret()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        var kept = await harness.Runner().ConfigureAsync(OidcInput() with { DisplayName = null, Secret = null }, harness.Connection!.Version,
            CancellationToken.None);
        Assert.Equal(FederationResultKind.Applied, kept.Value!.Receipt.Outcome);
        Assert.Null(harness.LastWrite!.Secret);
        Assert.Equal(1, harness.Provider.SecretWrites);
        Assert.True(harness.Provider.Find(harness.Connection.Alias)!.HasSecret);
    }

    [Fact]
    public async Task A_changed_configuration_the_provider_never_confirmed_still_needs_a_fresh_secret()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        harness.Provider.OnApply = (_, _) => Task.FromResult<FederationProviderResult?>(FederationProviderResult.Refused);
        var refusedWrite = await harness.Runner().ConfigureAsync(ManualInput(ChangedToken, secret: "rotated-s3cret"), harness.Connection!.Version,
            CancellationToken.None);
        Assert.Equal(FederationResultKind.Failed, refusedWrite.Value!.Receipt.Outcome);
        harness.Provider.OnApply = null;
        // The row now names the changed configuration, but the provider still holds the old endpoint and its secret.
        var retry = await harness.Runner().ConfigureAsync(ManualInput(ChangedToken, secret: null), harness.Connection.Version, CancellationToken.None);
        Assert.Equal("validation_failed", retry.Error!.Code);
        Assert.Equal(1, harness.Provider.SecretWrites);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("provider")]
    public async Task A_provider_fault_after_Begin_settles_uncertain_through_Complete(string fault)
    {
        using var harness = new FederationHarness();
        Exception failure = fault == "http" ? new HttpRequestException("connection reset")
            : new ProviderException("keycloak", "admin API failed", isClientError: false);
        harness.Provider.OnApply = (_, _) => throw failure;
        var run = await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        Assert.True(run.IsSuccess);
        Assert.Equal(FederationResultKind.Uncertain, run.Value!.Receipt.Outcome);
        Assert.Equal(["begin:BeginConfigureFederationCommand", "provider:apply", "complete:Uncertain"], harness.Log);
        Assert.True(harness.Connection!.HasPendingOperation, "an uncertain write stays pending for Resume");
        Assert.Equal(harness.Connection.Version, run.Value.Receipt.Version);
    }

    [Fact]
    public async Task A_provider_fault_on_a_readback_is_unavailable_not_an_exception()
    {
        using var harness = new FederationHarness();
        await harness.Runner().ConfigureAsync(OidcInput(), null, CancellationToken.None);
        harness.Provider.OnRead = (_, _) => throw new HttpRequestException("connection reset");
        var detail = await harness.Runner().ReadAsync(CancellationToken.None);
        Assert.Equal(FederationProviderObservation.Unavailable, detail.Value!.Provider);
        var activate = await harness.Runner().ActivateAsync(harness.Connection!.Version, CancellationToken.None);
        Assert.Equal("dependency_unavailable", activate.Error!.Code);
    }

    private static FederationConfigureInput ManualInput(Uri token, string? secret)
    {
        FederationClientSecret? value = null;
        if (secret is not null) Assert.True(FederationClientSecret.TryCreate(secret, out value));
        var issuer = InMemoryFederationEgressFetcher.Issuer;
        return new(FederationBrokerProtocol.Oidc, null, FederationSource.Manual, null, issuer, new Uri(issuer + "/protocol/openid-connect/auth"),
            token, new Uri(issuer + "/protocol/openid-connect/certs"), new Uri(issuer + "/protocol/openid-connect/userinfo"), null, [],
            "client", FederationClientAuthentication.ClientSecretBasic, value);
    }

    private static FederationConfigureInput SamlInput() => new(FederationBrokerProtocol.Saml, "Acme SAML", FederationSource.MetadataUrl,
        InMemoryFederationEgressFetcher.Metadata, null, null, null, null, null, null, ImmutableArray<string>.Empty, null,
        FederationClientAuthentication.ClientSecretBasic, null);
}
