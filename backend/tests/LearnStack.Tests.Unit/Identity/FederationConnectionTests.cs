using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>ADR-0099 § Lifecycle and test, the derived alias, the display-name rule and the pending/test invariants.</summary>
public sealed class FederationConnectionTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly UserId Other = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly DateTimeOffset At = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
    private const string Issuer = "https://idp.example.test/realms/school";

    [Fact]
    public void Window_constants_are_the_ADR_values()
    {
        Assert.Equal(TimeSpan.FromSeconds(90), FederationConnection.QuietWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), FederationConnection.TestWindow);
        Assert.Equal(TimeSpan.FromMinutes(10), FederationConnection.ActivationWindow);
    }

    [Theory]
    [InlineData("0193a0b4-1c2d-7e3f-8a4b-5c6d7e8f9a0b", "lsf-0193a0b41c2d7e3f8a4b5c6d7e8f9a0b")]
    [InlineData("01930000-0000-7000-8000-000000000001", "lsf-01930000000070008000000000000001")]
    [InlineData("FFFFFFFF-FFFF-7FFF-BFFF-FFFFFFFFFFFF", "lsf-ffffffffffff7fffbfffffffffffffff")]
    public void Alias_is_lsf_and_the_identifiers_32_lowercase_hex_characters(string id, string alias)
    {
        var connectionId = FederationConnectionId.From(Guid.Parse(id));
        Assert.Equal(alias, FederationConnection.DeriveAlias(connectionId));
        Assert.Equal(FederationConnection.AliasLength, alias.Length);
        Assert.Matches("^lsf-[0-9a-f]{32}$", alias);
        var connection = New(connectionId);
        Assert.Equal(alias, connection.Alias);
    }

    [Fact]
    public void A_recreated_connection_has_a_new_alias_and_the_alias_never_changes()
    {
        var first = New();
        var second = New();
        Assert.NotEqual(first.Alias, second.Alias);
        var alias = first.Alias;
        Apply(first);
        Assert.True(first.BeginRename("Renamed", Digest('c'), Operation(), At.AddMinutes(1), Actor).IsSuccess);
        Assert.True(first.BeginConfigure(FederationOperationKind.Configure, Config(digest: 'd'), Operation(), At.AddMinutes(2), Actor).IsFailure,
            "a pending rename blocks another Begin");
        Assert.Equal(alias, first.Alias);
    }

    [Theory]
    [InlineData("Acme SSO", "Acme SSO")]
    [InlineData("  Acme  ", "Acme")]
    [InlineData("Okul Giri\u015fi", "Okul Giri\u015fi")]
    [InlineData("Cafe\u0301", "Caf\u00e9")]
    [InlineData("\u65e5\u672c\u8a9e\u30ed\u30b0\u30a4\u30f3", "\u65e5\u672c\u8a9e\u30ed\u30b0\u30a4\u30f3")]
    [InlineData("A", "A")]
    [InlineData("Emoji \U0001F393 school", "Emoji \U0001F393 school")]
    [InlineData("I\u015f\u0131k \u0130\u015fletme", "I\u015f\u0131k \u0130\u015fletme")]
    [InlineData("\u0131\u0130\u015f\u015e\u011f\u011e\u00fc\u00dc\u00f6\u00d6\u00e7\u00c7", "\u0131\u0130\u015f\u015e\u011f\u011e\u00fc\u00dc\u00f6\u00d6\u00e7\u00c7")]
    [InlineData("\u00c7a\u011flayan \u00d6\u011fretim \u00dcniversitesi", "\u00c7a\u011flayan \u00d6\u011fretim \u00dcniversitesi")]
    [InlineData("Acme \u2764\ufe0f", "Acme \u2764\ufe0f")]
    public void Display_name_accepts_and_normalizes_visible_text(string input, string expected)
    {
        Assert.True(FederationDisplayName.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void Display_name_accepts_exactly_eighty_scalar_values_including_astral_ones()
    {
        Assert.True(FederationDisplayName.TryNormalize(new string('a', 80), out _));
        Assert.True(FederationDisplayName.TryNormalize(string.Concat(Enumerable.Repeat("\U0001F393", 80)), out _));
        Assert.False(FederationDisplayName.TryNormalize(new string('a', 81), out _));
        Assert.False(FederationDisplayName.TryNormalize(string.Concat(Enumerable.Repeat("\U0001F393", 81)), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Acme\u0000SSO")]
    [InlineData("Acme\tSSO")]
    [InlineData("Acme\nSSO")]
    [InlineData("Acme\u007fSSO")]
    [InlineData("Acme\u0085SSO")]
    [InlineData("Acme\u200bSSO")]
    [InlineData("Acme\u200eSSO")]
    [InlineData("Acme\u200fSSO")]
    [InlineData("\u202eosS emcA")]
    [InlineData("Acme\u202aSSO")]
    [InlineData("Acme\u2066SSO\u2069")]
    [InlineData("Acme\u061cSSO")]
    [InlineData("Acme\ufeffSSO")]
    [InlineData("Acme\u2028SSO")]
    [InlineData("Acme\u2029SSO")]
    [InlineData("Acme\u00a0SSO")]
    [InlineData("Acme\u3000SSO")]
    [InlineData("Acme\ue000SSO")]
    // Letters and a symbol by category that render blank, alone or inside a name.
    [InlineData("\u115f")]
    [InlineData("Acme\u115fSSO")]
    [InlineData("\u1160")]
    [InlineData("Acme\u1160SSO")]
    [InlineData("\u3164")]
    [InlineData("Acme\u3164SSO")]
    [InlineData("\uffa0")]
    [InlineData("Acme\uffa0SSO")]
    [InlineData("\u2800")]
    [InlineData("Acme\u2800SSO")]
    // A combining mark or variation selector as the first character, including after trimming.
    [InlineData("\u0301Acme")]
    [InlineData(" \u0301Acme")]
    [InlineData("\u0903Acme")]
    [InlineData("\u20ddAcme")]
    [InlineData("\u064eAcme")]
    [InlineData("\ufe00Acme")]
    [InlineData("\ufe0fAcme")]
    [InlineData("\U000E0100Acme")]
    [InlineData("\U000E01EFAcme")]
    public void Display_name_refuses_control_format_bidirectional_separator_and_invalid_characters(string? input)
    {
        Assert.False(FederationDisplayName.TryNormalize(input, out var normalized));
        Assert.Equal(string.Empty, normalized);
    }

    [Fact]
    public void Display_name_refuses_unpaired_surrogates_without_throwing()
    {
        // Theory data would serialize a lone surrogate to U+FFFD, so these vectors stay in code.
        foreach (var input in new[] { "Acme\ud800SSO", "Acme\udc00SSO", "\ud83c", "SSO\udf93" })
        {
            Assert.False(FederationDisplayName.TryNormalize(input, out var normalized));
            Assert.Equal(string.Empty, normalized);
        }
    }

    [Fact]
    public void Create_starts_Draft_with_one_pending_Configure_and_no_test()
    {
        var connection = New();
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        Assert.Equal(1, connection.ConfigurationRevision);
        Assert.Equal(Issuer, connection.IssuerOrEntityId);
        Assert.Equal(FederationOperationKind.Configure, connection.PendingOperationKind);
        Assert.Equal(Actor, connection.PendingActor);
        Assert.Equal(At, connection.PendingStartedAt);
        Assert.Equal(At + FederationConnection.QuietWindow, connection.PendingQuietUntil);
        Assert.Equal(connection.DesiredDigest, connection.PendingDesiredDigest);
        Assert.Null(connection.LastProviderDigest);
        AssertNoTest(connection);
        Assert.Equal(0, connection.Version);
    }

    [Fact]
    public void Create_refuses_invalid_identity_tenant_protocol_client_digest_and_actor()
    {
        var id = FederationConnectionId.From(Guid.CreateVersion7());
        Assert.True(Create(FederationConnectionId.From(Guid.NewGuid()), Tenant, FederationProtocol.Oidc, Config()).IsFailure);
        Assert.True(Create(id, TenantId.PlatformSentinel, FederationProtocol.Oidc, Config()).IsFailure);
        Assert.True(Create(id, Tenant, (FederationProtocol)9, Config()).IsFailure);
        Assert.True(Create(id, Tenant, FederationProtocol.Oidc, Config(clientId: null)).IsFailure);
        Assert.True(Create(id, Tenant, FederationProtocol.Saml, Config()).IsFailure, "SAML has no client identifier");
        Assert.True(Create(id, Tenant, FederationProtocol.Saml, Config(clientId: null)).IsSuccess);
        Assert.True(Create(id, Tenant, FederationProtocol.Oidc, Config() with { ConfigurationDigest = "ABC" }).IsFailure);
        Assert.True(Create(id, Tenant, FederationProtocol.Oidc, Config() with { IssuerOrEntityId = "has space" }).IsFailure);
        Assert.True(Create(id, Tenant, FederationProtocol.Oidc, Config() with { IssuerOrEntityId = new string('a', 2049) }).IsFailure);
        Assert.True(FederationConnection.Create(id, Tenant, FederationProtocol.Oidc, "Acme", Config(), Operation(), At, UserId.SystemActor).IsFailure);
        Assert.True(FederationConnection.Create(id, Tenant, FederationProtocol.Oidc, "Acme\u202e", Config(), Operation(), At, Actor).IsFailure);
    }

    [Theory]
    [InlineData(FederationProtocol.Oidc, "https://user:pass@idp.example.test/realms/school", false)]
    [InlineData(FederationProtocol.Oidc, "https://user@idp.example.test", false)]
    [InlineData(FederationProtocol.Oidc, "https://idp.example.test/realms/school?x", false)]
    [InlineData(FederationProtocol.Oidc, "https://idp.example.test/realms/school#y", false)]
    [InlineData(FederationProtocol.Oidc, "https://idp.example.test?x", false)]
    [InlineData(FederationProtocol.Oidc, "https://idp.example.test/realms/school", true)]
    [InlineData(FederationProtocol.Oidc, "https://idp.example.test:8443/tenants/a@b", true)]
    [InlineData(FederationProtocol.Oidc, "https://idp.example.test", true)]
    [InlineData(FederationProtocol.Saml, "urn:example:idp?x#y@z", true)]
    [InlineData(FederationProtocol.Saml, "https://user@idp.example.test/saml?entity=1#frag", true)]
    public void OIDC_issuer_refuses_userinfo_query_and_fragment_and_SAML_entity_identifier_stays_free_form(
        FederationProtocol protocol, string issuer, bool admitted)
    {
        var configuration = Config(clientId: protocol == FederationProtocol.Oidc ? "learnstack-client" : null) with { IssuerOrEntityId = issuer };
        Assert.Equal(admitted, Create(FederationConnectionId.From(Guid.CreateVersion7()), Tenant, protocol, configuration).IsSuccess);
    }

    [Fact]
    public void Issuer_or_entity_identifier_cannot_change_in_place()
    {
        var connection = Applied();
        var before = connection.Version;
        var changed = connection.BeginConfigure(FederationOperationKind.Configure,
            Config(digest: 'e') with { IssuerOrEntityId = "https://other.example.test/" }, Operation(), At.AddMinutes(1), Actor);
        Assert.True(changed.IsFailure);
        Assert.Equal("lockey_business_rule_violation", changed.Error!.Message.Key);
        Assert.Equal(Issuer, connection.IssuerOrEntityId);
        Assert.Equal(before, connection.Version);
        Assert.False(connection.HasPendingOperation);
        Assert.True(connection.BeginConfigure(FederationOperationKind.RotateSecret, Config(digest: 'e'), Operation(), At.AddMinutes(2), Actor).IsSuccess);
        Assert.Equal(2, connection.ConfigurationRevision);
    }

    [Fact]
    public void Full_happy_lifecycle_walks_every_legal_state()
    {
        var connection = Applied();
        var start = Operation();
        Assert.True(connection.BeginTestStart(Digest('b'), start, At.AddMinutes(1), Other).IsSuccess);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        var attempt = Attempt();
        Assert.True(connection.Complete(start, FederationOperationOutcome.Applied, Digest('b'), attempt, At.AddMinutes(1.5), Actor).Value);
        Assert.Equal(FederationConnectionState.TestOpen, connection.State);
        Assert.Equal(attempt, connection.TestAttemptId);
        Assert.Equal(Other, connection.TestActor);
        Assert.Equal(FederationTestResult.Pending, connection.TestResult);
        Assert.Equal(At.AddMinutes(1.5) + FederationConnection.TestWindow, connection.TestExpiresAt);
        Assert.Equal(connection.ConfigurationRevision, connection.TestRevision);
        Assert.True(connection.RecordTestResult(attempt, passed: true, At.AddMinutes(3), Other).IsSuccess);
        Assert.Equal(FederationConnectionState.TestPassed, connection.State);
        Assert.Equal(At.AddMinutes(3), connection.TestPassedAt);
        Assert.Equal(At.AddMinutes(3) + FederationConnection.ActivationWindow, connection.TestExpiresAt);
        Assert.True(connection.Activate(Digest('b'), At.AddMinutes(4), Actor).IsSuccess);
        Assert.Equal(FederationConnectionState.Active, connection.State);
        AssertNoTest(connection);
        var disable = Operation();
        Assert.True(connection.BeginDisable(Digest('c'), disable, At.AddMinutes(5), Actor).IsSuccess);
        Assert.Equal(FederationConnectionState.Disabled, connection.State);
        Assert.True(connection.Complete(disable, FederationOperationOutcome.Applied, Digest('c'), null, At.AddMinutes(5.5), Actor).Value);
        Assert.True(connection.Enable(At.AddMinutes(6), Actor).IsSuccess);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        var delete = Operation();
        Assert.True(connection.BeginDelete(Digest('d'), delete, At.AddMinutes(7), Actor).IsSuccess);
        var beforeTombstone = connection.Version;
        Assert.True(connection.Complete(delete, FederationOperationOutcome.Applied, Digest('d'), null, At.AddMinutes(7.5), Actor).Value);
        Assert.Equal(beforeTombstone + 1, connection.Version);
        Assert.Equal(FederationConnectionState.Deleted, connection.State);
        Assert.True(connection.IsDeleted);
        Assert.Equal(At.AddMinutes(7.5), connection.DeletedAt);
        Assert.Equal(Actor, connection.DeletedBy);
        Assert.False(connection.HasPendingOperation);
    }

    [Fact]
    public void Deleted_tombstone_is_terminal_and_keeps_its_alias()
    {
        var connection = Applied();
        var alias = connection.Alias;
        var delete = Operation();
        Assert.True(connection.BeginDelete(Digest('d'), delete, At.AddMinutes(1), Actor).IsSuccess);
        Assert.True(connection.Complete(delete, FederationOperationOutcome.Applied, Digest('d'), null, At.AddMinutes(2), Actor).Value);
        var version = connection.Version;
        Assert.True(connection.BeginConfigure(FederationOperationKind.Configure, Config(digest: 'e'), Operation(), At.AddMinutes(3), Actor).IsFailure);
        Assert.True(connection.BeginRename("New", Digest('e'), Operation(), At.AddMinutes(3), Actor).IsFailure);
        Assert.True(connection.BeginTestStart(Digest('e'), Operation(), At.AddMinutes(3), Actor).IsFailure);
        Assert.True(connection.BeginDisable(Digest('e'), Operation(), At.AddMinutes(3), Actor).IsFailure);
        Assert.True(connection.BeginDelete(Digest('e'), Operation(), At.AddMinutes(3), Actor).IsFailure);
        Assert.True(connection.Enable(At.AddMinutes(3), Actor).IsFailure);
        Assert.True(connection.RecordDrift(Digest('f'), At.AddMinutes(3), Actor).IsFailure);
        Assert.True(connection.Complete(delete, FederationOperationOutcome.Applied, Digest('d'), null, At.AddMinutes(3), Actor).IsFailure);
        Assert.Equal(version, connection.Version);
        Assert.Equal(alias, connection.Alias);
        Assert.Equal(FederationConnectionState.Deleted, connection.State);
    }

    [Theory]
    [InlineData(FederationConnectionState.TestOpen)]
    [InlineData(FederationConnectionState.TestPassed)]
    [InlineData(FederationConnectionState.Active)]
    public void Delete_from_an_enabled_state_stops_admission_before_the_provider_write(FederationConnectionState state)
    {
        var connection = InState(state);
        Assert.True(connection.BeginDelete(Digest('d'), Operation(), At.AddMinutes(9), Actor).IsSuccess);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        AssertNoTest(connection);
    }

    [Fact]
    public void Failed_Delete_keeps_the_row_and_settles_the_operation()
    {
        var connection = Applied();
        var delete = Operation();
        Assert.True(connection.BeginDelete(Digest('d'), delete, At.AddMinutes(1), Actor).IsSuccess);
        Assert.True(connection.Complete(delete, FederationOperationOutcome.Failed, null, null, At.AddMinutes(2), Actor).Value);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        Assert.False(connection.IsDeleted);
        Assert.False(connection.HasPendingOperation);
    }

    public static TheoryData<FederationConnectionState, string> IllegalTransitions => new()
    {
        { FederationConnectionState.Draft, "record_test" },
        { FederationConnectionState.Draft, "activate" },
        { FederationConnectionState.Draft, "disable" },
        { FederationConnectionState.Draft, "enable" },
        { FederationConnectionState.Draft, "expire" },
        { FederationConnectionState.TestOpen, "test_start" },
        { FederationConnectionState.TestOpen, "activate" },
        { FederationConnectionState.TestOpen, "disable" },
        { FederationConnectionState.TestOpen, "enable" },
        { FederationConnectionState.TestPassed, "test_start" },
        { FederationConnectionState.TestPassed, "record_test" },
        { FederationConnectionState.TestPassed, "disable" },
        { FederationConnectionState.TestPassed, "enable" },
        { FederationConnectionState.Active, "test_start" },
        { FederationConnectionState.Active, "record_test" },
        { FederationConnectionState.Active, "activate" },
        { FederationConnectionState.Active, "enable" },
        { FederationConnectionState.Active, "expire" },
        { FederationConnectionState.Disabled, "test_start" },
        { FederationConnectionState.Disabled, "record_test" },
        { FederationConnectionState.Disabled, "activate" },
        { FederationConnectionState.Disabled, "disable" },
        { FederationConnectionState.Disabled, "expire" },
    };

    [Theory]
    [MemberData(nameof(IllegalTransitions))]
    public void Illegal_transitions_are_business_refusals_without_mutation(FederationConnectionState state, string action)
    {
        var connection = InState(state);
        var version = connection.Version;
        var attempt = connection.TestAttemptId ?? Attempt();
        var at = At.AddMinutes(8);
        var result = action switch
        {
            "test_start" => connection.BeginTestStart(Digest('e'), Operation(), at, Actor),
            "record_test" => connection.RecordTestResult(attempt, true, at, Actor),
            "activate" => connection.Activate(connection.DesiredDigest, at, Actor),
            "disable" => connection.BeginDisable(Digest('e'), Operation(), at, Actor),
            "enable" => connection.Enable(at, Actor),
            "expire" => connection.ExpireTest(at, Actor),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        Assert.True(result.IsFailure);
        Assert.Equal("lockey_business_rule_violation", result.Error!.Message.Key);
        Assert.Equal(state, connection.State);
        Assert.Equal(version, connection.Version);
    }

    [Theory]
    [InlineData(FederationConnectionState.TestOpen)]
    [InlineData(FederationConnectionState.TestPassed)]
    [InlineData(FederationConnectionState.Active)]
    [InlineData(FederationConnectionState.Disabled)]
    public void Configure_and_rotate_return_any_live_state_to_Draft_and_clear_the_test(FederationConnectionState state)
    {
        foreach (var kind in new[] { FederationOperationKind.Configure, FederationOperationKind.RotateSecret })
        {
            var connection = InState(state);
            var revision = connection.ConfigurationRevision;
            Assert.True(connection.BeginConfigure(kind, Config(digest: 'e'), Operation(), At.AddMinutes(9), Actor).IsSuccess);
            Assert.Equal(FederationConnectionState.Draft, connection.State);
            Assert.Equal(revision + 1, connection.ConfigurationRevision);
            Assert.Equal(kind, connection.PendingOperationKind);
            AssertNoTest(connection);
        }
    }

    [Fact]
    public void Test_failure_and_expiry_return_to_Draft()
    {
        var failed = InState(FederationConnectionState.TestOpen);
        Assert.True(failed.RecordTestResult(failed.TestAttemptId!.Value, passed: false, At.AddMinutes(3), Actor).IsSuccess);
        Assert.Equal(FederationConnectionState.Draft, failed.State);
        AssertNoTest(failed);

        var open = InState(FederationConnectionState.TestOpen);
        Assert.True(open.ExpireTest(open.TestExpiresAt!.Value.AddTicks(-10), Actor).IsFailure, "an open window is not expired");
        Assert.True(open.RecordTestResult(open.TestAttemptId!.Value, true, open.TestExpiresAt!.Value, Actor).IsFailure,
            "a result at or after expiry is refused");
        Assert.True(open.ExpireTest(open.TestExpiresAt!.Value, Actor).IsSuccess);
        Assert.Equal(FederationConnectionState.Draft, open.State);

        var passed = InState(FederationConnectionState.TestPassed);
        Assert.True(passed.Activate(passed.DesiredDigest, passed.TestExpiresAt!.Value, Actor).IsFailure, "activation window elapsed");
        Assert.True(passed.ExpireTest(passed.TestExpiresAt!.Value, Actor).IsSuccess);
        Assert.Equal(FederationConnectionState.Draft, passed.State);
    }

    [Fact]
    public void Test_result_requires_the_current_attempt_and_activation_requires_a_matching_readback()
    {
        var connection = InState(FederationConnectionState.TestOpen);
        Assert.True(connection.RecordTestResult(Attempt(), true, At.AddMinutes(3), Actor).IsFailure);
        Assert.Equal(FederationConnectionState.TestOpen, connection.State);
        Assert.True(connection.RecordTestResult(connection.TestAttemptId!.Value, true, At.AddMinutes(3), Actor).IsSuccess);
        Assert.True(connection.Activate(Digest('f'), At.AddMinutes(4), Actor).IsFailure, "readback differs from the desired digest");
        Assert.Equal(FederationConnectionState.TestPassed, connection.State);
    }

    [Fact]
    public void One_provider_write_is_unsettled_at_a_time_and_Resume_waits_for_the_quiet_window()
    {
        var connection = New();
        var first = connection.PendingOperationId!.Value;
        Assert.True(connection.BeginRename("Other", Digest('c'), Operation(), At.AddMinutes(1), Actor).IsFailure);
        Assert.True(connection.BeginDelete(Digest('c'), Operation(), At.AddMinutes(1), Actor).IsFailure);
        Assert.True(connection.Complete(first, FederationOperationOutcome.Uncertain, null, null, At.AddSeconds(30), Actor).Value);
        Assert.True(connection.HasPendingOperation, "uncertain is settled as a result but remains resumable");
        Assert.Equal(first, connection.PendingOperationId);
        Assert.False(connection.CanResume(At.AddSeconds(89)));
        Assert.True(connection.CanResume(At.AddSeconds(90)));
        Assert.True(connection.Complete(first, FederationOperationOutcome.Applied, connection.DesiredDigest, null, At.AddMinutes(2), Other).Value);
        Assert.False(connection.HasPendingOperation);
        Assert.False(connection.CanResume(At.AddHours(1)));
        Assert.Null(connection.PendingActor);
        Assert.Null(connection.PendingQuietUntil);
    }

    [Fact]
    public void A_superseded_Complete_changes_nothing_and_Applied_requires_the_desired_readback()
    {
        var connection = New();
        var version = connection.Version;
        var stale = Operation();
        var superseded = connection.Complete(stale, FederationOperationOutcome.Applied, connection.DesiredDigest, null, At.AddSeconds(5), Actor);
        Assert.True(superseded.IsSuccess);
        Assert.False(superseded.Value);
        Assert.Equal(version, connection.Version);
        var pending = connection.PendingOperationId!.Value;
        Assert.True(connection.Complete(pending, FederationOperationOutcome.Applied, Digest('f'), null, At.AddSeconds(5), Actor).IsFailure);
        Assert.True(connection.Complete(pending, FederationOperationOutcome.Applied, connection.DesiredDigest, Attempt(), At.AddSeconds(5), Actor).IsFailure,
            "only a test start carries an attempt");
        Assert.True(connection.Complete(pending, FederationOperationOutcome.Failed, null, null, At.AddSeconds(5), Actor).Value);
        Assert.False(connection.HasPendingOperation);
        Assert.Null(connection.LastProviderDigest);
    }

    [Fact]
    public void Drift_returns_to_Draft_and_records_the_provider_digest()
    {
        var connection = InState(FederationConnectionState.Active);
        Assert.True(connection.RecordDrift(connection.DesiredDigest, At.AddMinutes(9), Actor).IsFailure, "equal readback is not drift");
        Assert.True(connection.RecordDrift(Digest('f'), At.AddMinutes(9), Actor).IsSuccess);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        Assert.Equal(Digest('f'), connection.LastProviderDigest);
    }

    [Fact]
    public void Drift_on_a_Disabled_connection_returns_it_to_Draft()
    {
        var connection = InState(FederationConnectionState.Disabled);
        Assert.True(connection.RecordDrift(Digest('f'), At.AddMinutes(9), Actor).IsSuccess);
        Assert.Equal(FederationConnectionState.Draft, connection.State);
        Assert.Equal(Digest('f'), connection.LastProviderDigest);
        AssertNoTest(connection);
        Assert.True(connection.Enable(At.AddMinutes(10), Actor).IsFailure, "a drifted connection is Draft, so Enable has nothing to move");
    }

    public static TheoryData<string> BackdatedMutators => new()
    {
        "complete_applied", "complete_failed", "complete_uncertain", "record_test", "expire", "activate", "enable", "drift",
    };

    [Theory]
    [MemberData(nameof(BackdatedMutators))]
    public void A_stamp_before_the_last_update_is_a_refusal_without_mutation_and_the_last_stamp_itself_is_admitted(string action)
    {
        foreach (var backdated in new[] { true, false })
        {
            var (connection, last) = Prepared(action);
            var at = backdated ? last.AddTicks(-1) : last;
            var state = connection.State;
            var version = connection.Version;
            var result = Act(connection, action, at);
            if (backdated)
            {
                Assert.True(result.IsFailure, action);
                Assert.Equal(state, connection.State);
                Assert.Equal(version, connection.Version);
                Assert.Equal(last, connection.UpdatedAt);
            }
            else Assert.True(result.IsSuccess, action + " at exactly the last stamp is monotonic");
        }

        static (FederationConnection Connection, DateTimeOffset Last) Prepared(string action)
        {
            switch (action)
            {
                case "complete_applied" or "complete_failed" or "complete_uncertain":
                    {
                        // Uncertain advances the stamp past the pending start, so a later Complete can be backdated
                        // while still being after the start the existing check compares with.
                        var connection = New();
                        Assert.True(connection.Complete(connection.PendingOperationId!.Value, FederationOperationOutcome.Uncertain, null, null,
                            At.AddSeconds(30), Actor).Value);
                        return (connection, At.AddSeconds(30));
                    }
                case "record_test":
                    return (InState(FederationConnectionState.TestOpen), At.AddMinutes(2));
                case "expire":
                    {
                        // A rename after the window has elapsed moves the stamp beyond the expiry.
                        var connection = InState(FederationConnectionState.TestOpen);
                        var last = connection.TestExpiresAt!.Value.AddMinutes(1);
                        Assert.True(connection.BeginRename("Renamed", Digest('c'), Operation(), last, Actor).IsSuccess);
                        return (connection, last);
                    }
                case "activate":
                    return (InState(FederationConnectionState.TestPassed), At.AddMinutes(3));
                case "enable":
                    return (InState(FederationConnectionState.Disabled), At.AddMinutes(6));
                default:
                    return (InState(FederationConnectionState.Active), At.AddMinutes(4));
            }
        }

        static LearnStack.SharedKernel.Results.IResultBase Act(FederationConnection connection, string action, DateTimeOffset at) => action switch
        {
            "complete_applied" => connection.Complete(connection.PendingOperationId!.Value, FederationOperationOutcome.Applied,
                connection.PendingDesiredDigest, null, at, Actor),
            "complete_failed" => connection.Complete(connection.PendingOperationId!.Value, FederationOperationOutcome.Failed, null, null, at, Actor),
            "complete_uncertain" => connection.Complete(connection.PendingOperationId!.Value, FederationOperationOutcome.Uncertain, null, null, at, Actor),
            "record_test" => connection.RecordTestResult(connection.TestAttemptId!.Value, true, at, Actor),
            "expire" => connection.ExpireTest(at, Actor),
            "activate" => connection.Activate(connection.DesiredDigest, at, Actor),
            "enable" => connection.Enable(at, Actor),
            "drift" => connection.RecordDrift(Digest('f'), at, Actor),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
    }

    [Fact]
    public void Rename_keeps_the_state_and_validates_the_name()
    {
        var connection = InState(FederationConnectionState.Active);
        Assert.True(connection.BeginRename("\u202eevil", Digest('e'), Operation(), At.AddMinutes(9), Actor).IsFailure);
        Assert.True(connection.BeginRename("  Okul SSO ", Digest('e'), Operation(), At.AddMinutes(9), Actor).IsSuccess);
        Assert.Equal("Okul SSO", connection.DisplayName);
        Assert.Equal(FederationConnectionState.Active, connection.State);
    }

    private static FederationConnection InState(FederationConnectionState state)
    {
        var connection = Applied();
        if (state == FederationConnectionState.Draft) return connection;
        var start = Operation();
        Assert.True(connection.BeginTestStart(Digest('b'), start, At.AddMinutes(1), Actor).IsSuccess);
        Assert.True(connection.Complete(start, FederationOperationOutcome.Applied, Digest('b'), Attempt(), At.AddMinutes(2), Actor).Value);
        if (state == FederationConnectionState.TestOpen) return connection;
        Assert.True(connection.RecordTestResult(connection.TestAttemptId!.Value, true, At.AddMinutes(3), Actor).IsSuccess);
        if (state == FederationConnectionState.TestPassed) return connection;
        Assert.True(connection.Activate(Digest('b'), At.AddMinutes(4), Actor).IsSuccess);
        if (state == FederationConnectionState.Active) return connection;
        var disable = Operation();
        Assert.True(connection.BeginDisable(Digest('c'), disable, At.AddMinutes(5), Actor).IsSuccess);
        Assert.True(connection.Complete(disable, FederationOperationOutcome.Applied, Digest('c'), null, At.AddMinutes(6), Actor).Value);
        Assert.Equal(FederationConnectionState.Disabled, connection.State);
        return connection;
    }

    private static FederationConnection Applied()
    {
        var connection = New();
        Apply(connection);
        return connection;
    }

    private static void Apply(FederationConnection connection) => Assert.True(connection.Complete(connection.PendingOperationId!.Value,
        FederationOperationOutcome.Applied, connection.PendingDesiredDigest, null, At.AddSeconds(10), Actor).Value);

    private static FederationConnection New(FederationConnectionId? id = null) =>
        Create(id ?? FederationConnectionId.From(Guid.CreateVersion7()), Tenant, FederationProtocol.Oidc, Config()).Value!;

    private static LearnStack.SharedKernel.Results.Result<FederationConnection> Create(FederationConnectionId id, TenantId tenant,
        FederationProtocol protocol, FederationConfiguration configuration) =>
        FederationConnection.Create(id, tenant, protocol, "Acme SSO", configuration, Operation(), At, Actor);

    private static FederationConfiguration Config(char digest = 'a', string? clientId = "learnstack-client") =>
        new(FederationSourceKind.DiscoveryUrl, Issuer, clientId, Digest(digest), Digest(digest));

    private static string Digest(char value) => new(value, 64);
    private static FederationOperationId Operation() => FederationOperationId.From(Guid.CreateVersion7());
    private static FederationTestAttemptId Attempt() => FederationTestAttemptId.From(Guid.CreateVersion7());

    private static void AssertNoTest(FederationConnection connection)
    {
        Assert.Null(connection.TestAttemptId);
        Assert.Null(connection.TestActor);
        Assert.Null(connection.TestRevision);
        Assert.Null(connection.TestExpiresAt);
        Assert.Null(connection.TestResult);
        Assert.Null(connection.TestPassedAt);
    }
}
