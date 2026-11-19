using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The controlled issuer rebind end to end on real PostgreSQL as the confined role (ADR-0104): the positive run,
/// every refusal class with its state and audit trail, the continuity rules, the freeze, and the failure windows.
/// </summary>
[Collection(PublicReadTestGroup.Name)]
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class IdentityAuthorityRebindTests(PublicReadFixture parent)
{
    private const string NewIssuer = "https://new.example.test/realms/learnstack";

    [Fact]
    public async Task A_Rebind_Changes_Only_The_Issuer_And_Records_An_Intent_And_A_Result_Row()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 3);
        var before = await proof.UsersAsync();
        var admission = RebindProofSupport.Admit();

        var outcome = await proof.RunAsync(admission, FixedRealm.Reporting([.. proof.Subjects, "brand-new-person"]));

        outcome.Result.Should().Be(IdentityRebindResult.Applied);
        outcome.Continuity.Should().BeEquivalentTo(new IdentityRebindContinuityResult(3, 0, 0, 4, 0, 0, 0, 1));
        (await proof.IssuerAsync()).Should().Be(NewIssuer);
        (await proof.AuthorityAsync()).Should().Be(proof.Authority);
        (await proof.UsersAsync()).Should().Equal(before, "no actor id, key or subject moved");

        var rows = await proof.AuditRowsAsync(admission);
        rows.Select(row => (row.Operation, row.Outcome)).Should().Equal(
            ("identity.authority.rebind_attempt", "indeterminate"), ("identity.authority.rebind", "success"));
        rows.Should().OnlyContain(row => row.Tenant == TenantId.PlatformSentinel.Value && row.Actor == null
            && row.Reason == "issuer_rebind" && row.Entity == proof.Authority.ToString("D"));
        rows.Should().OnlyContain(row => row.Operator.GetProperty("ticket").GetString() == "CHG-104"
            && row.Operator.GetProperty("subjectDigest").GetString() == admission.Operator.SubjectDigest);
        rows[1].Before.GetProperty("issuer").GetString().Should().Be(proof.OldIssuer);
        rows[1].After.GetProperty("issuer").GetString().Should().Be(NewIssuer);
        rows[1].Continuity.GetProperty("active").GetInt32().Should().Be(3);
        rows[1].Continuity.GetProperty("unbootstrapped").GetInt32().Should().Be(1);
        rows[1].Raw.Should().NotContain(proof.Subjects[0], "no subject reaches an audit row");
    }

    [Fact]
    public async Task A_Run_Whose_Target_Already_Equals_The_Stored_Issuer_Writes_Nothing_Not_Even_A_Row()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        var first = RebindProofSupport.Admit();
        (await proof.RunAsync(first, FixedRealm.Reporting(proof.Subjects))).Result.Should().Be(IdentityRebindResult.Applied);

        var again = RebindProofSupport.Admit();
        var realm = FixedRealm.Reporting(proof.Subjects);
        var outcome = await proof.RunAsync(again, realm);

        outcome.Result.Should().Be(IdentityRebindResult.AlreadyApplied);
        realm.Reads.Should().Be(0, "the new realm is not even read");
        (await proof.AuditRowsAsync(again)).Should().BeEmpty();
        (await proof.AuditRowsAsync(first)).Should().HaveCount(2);
    }

    [Fact]
    public async Task An_Admission_For_Another_Operation_Or_A_Stale_Or_A_Reused_One_Is_Refused_Before_Any_Connection()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);

        var killswitch = TestPlatformAdmissions.Admission(PlatformOperations.KillswitchToggle, correlationId: "00-x");
        (await proof.RunAsync(killswitch, FixedRealm.Reporting(proof.Subjects))).Refusal.Should().Be(IdentityRebindRefusal.NotAdmitted);

        var stale = RebindProofSupport.Admit(issuedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        (await proof.RunAsync(stale, FixedRealm.Reporting(proof.Subjects))).Refusal.Should().Be(IdentityRebindRefusal.NotAdmitted);

        var used = RebindProofSupport.Admit();
        (await proof.RunAsync(used, FixedRealm.Reporting(proof.Subjects))).Result.Should().Be(IdentityRebindResult.Applied);
        (await proof.RunAsync(used, FixedRealm.Reporting(proof.Subjects))).Refusal.Should().Be(IdentityRebindRefusal.NotAdmitted,
            "an admission is spent by its first run");
    }

    [Theory]
    [InlineData("learnstack_app")]
    [InlineData("learnstack_migration")]
    [InlineData("learnstack_platform")]
    public async Task Any_Role_But_The_Rebind_Role_Is_Refused_Before_Its_First_Statement(string role)
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        var connection = new NpgsqlConnectionStringBuilder(proof.RebindConnection) { Username = role }.ConnectionString;

        var outcome = await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(proof.Subjects), connection: connection);

        outcome.Refusal.Should().Be(IdentityRebindRefusal.RoleInvalid);
        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer);
    }

    [Theory]
    // Role attributes and memberships are cluster-wide and need the superuser; object grants are this database's own.
    [InlineData(true, "ALTER ROLE learnstack_identity_rebind CREATEDB", "ALTER ROLE learnstack_identity_rebind NOCREATEDB")]
    [InlineData(true, "ALTER ROLE learnstack_identity_rebind INHERIT", "ALTER ROLE learnstack_identity_rebind NOINHERIT")]
    [InlineData(true, "GRANT learnstack_migration TO learnstack_identity_rebind", "REVOKE learnstack_migration FROM learnstack_identity_rebind")]
    [InlineData(false, "GRANT SELECT ON public.tenants TO learnstack_identity_rebind", "REVOKE SELECT ON public.tenants FROM learnstack_identity_rebind")]
    [InlineData(false, "GRANT UPDATE (authority_id) ON public.identity_authority TO learnstack_identity_rebind", "REVOKE UPDATE (authority_id) ON public.identity_authority FROM learnstack_identity_rebind")]
    [InlineData(false, "GRANT SELECT (id) ON public.users TO learnstack_identity_rebind", "REVOKE SELECT (id) ON public.users FROM learnstack_identity_rebind")]
    [InlineData(false, "GRANT SELECT ON public.audit_log TO learnstack_identity_rebind", "REVOKE SELECT ON public.audit_log FROM learnstack_identity_rebind")]
    [InlineData(false, "GRANT CREATE ON SCHEMA public TO learnstack_identity_rebind", "REVOKE CREATE ON SCHEMA public FROM learnstack_identity_rebind")]
    public async Task A_Role_That_Holds_Anything_Beyond_Its_Grants_Is_Refused_Before_The_Lock(bool clusterWide, string escalate, string restore)
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        // The collection runs serially against its own container, and every escalation is undone in the same test.
        await Apply(clusterWide, escalate);
        try
        {
            var outcome = await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(proof.Subjects));

            outcome.Refusal.Should().Be(IdentityRebindRefusal.RoleInvalid, escalate);
        }
        finally
        {
            await Apply(clusterWide, restore);
        }

        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer);

        async Task Apply(bool superuser, string sql)
        {
            if (superuser)
            {
                await parent.Postgres.ExecuteAsSuperuserAsync(sql);
                return;
            }

            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.OwnerConnection);
            await IdentityMembershipFixture.ExecuteAsync(owner, null, sql);
        }
    }

    [Fact]
    public async Task A_Rebind_That_Is_Already_Running_Makes_The_Second_One_Wait_Then_Refuse_Without_A_Row()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        await using var held = await proof.HoldRebindLockAsync();
        var admission = RebindProofSupport.Admit();

        var outcome = await proof.RunAsync(admission, FixedRealm.Reporting(proof.Subjects), lockWait: TimeSpan.FromSeconds(1));

        outcome.Refusal.Should().Be(IdentityRebindRefusal.RebindInProgress);
        (await proof.AuditRowsAsync(admission)).Should().BeEmpty("the lock is the first statement, so nothing was decided yet");
        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer);
    }

    [Fact]
    public async Task A_Stored_Authority_That_Is_Not_The_Configured_One_Is_Refused_After_The_Intent_Row()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        var admission = RebindProofSupport.Admit();
        var other = new IdentityAuthorityRegistration(IdentityAuthorityId.From(Guid.CreateVersion7()), NewIssuer);

        var outcome = await proof.RunAsync(admission, FixedRealm.Reporting(proof.Subjects), target: other);

        outcome.Refusal.Should().Be(IdentityRebindRefusal.AuthorityMismatch);
        (await proof.AuditRowsAsync(admission)).Should().ContainSingle().Which.Operation.Should().Be("identity.authority.rebind_attempt");
        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer);
        (await proof.AuthorityAsync()).Should().Be(proof.Authority, "a changed AuthorityId would re-key every person");
    }

    [Fact]
    public async Task The_Same_Issuer_Under_Another_Authority_Is_Not_Already_Applied_It_Is_A_Misconfiguration()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        var admission = RebindProofSupport.Admit();
        var other = new IdentityAuthorityRegistration(IdentityAuthorityId.From(Guid.CreateVersion7()), proof.OldIssuer);

        var outcome = await proof.RunAsync(admission, FixedRealm.Reporting(proof.Subjects), target: other);

        outcome.Result.Should().Be(IdentityRebindResult.Refused, "equal issuers do not make the run a no-op when the authority differs");
        outcome.Refusal.Should().Be(IdentityRebindRefusal.AuthorityMismatch);
        (await proof.AuditRowsAsync(admission)).Should().ContainSingle();
    }

    [Theory]
    [InlineData("learnstack_app")]
    [InlineData("learnstack_outbox_admin")]
    [InlineData("learnstack_identity_ingress")]
    [InlineData("learnstack_platform")]
    public async Task A_Live_Session_Of_A_Runtime_Role_Freezes_The_Rebind_And_Its_Disappearance_Lifts_It(string role)
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        var admission = RebindProofSupport.Admit();
        var realm = FixedRealm.Reporting(proof.Subjects);

        int? seen;
        await using (var session = await proof.OpenSessionAsync(role))
        {
            var refused = await proof.RunAsync(admission, realm);
            refused.Refusal.Should().Be(IdentityRebindRefusal.LiveSessions);
            seen = refused.LiveSessions;
        }

        seen.Should().Be(1, "the count is reported, never a session's identity");
        realm.Reads.Should().Be(0, "the freeze is checked before the new realm is read");
        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer);

        var retry = await proof.RunAsync(RebindProofSupport.Admit(), realm);
        retry.Result.Should().Be(IdentityRebindResult.Applied, "a retry repeats the whole proof against the quiet database");
    }

    [Fact]
    public async Task The_New_Realm_Must_Be_Readable_Name_Its_Own_Issuer_Publish_Keys_And_Be_Complete()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);

        var cases = new (string Why, FixedRealm Realm, IdentityRebindRefusal Expected)[]
        {
            ("unreadable", FixedRealm.Failing(), IdentityRebindRefusal.NewRealmUnavailable),
            ("another discovery issuer", FixedRealm.Reporting(proof.Subjects, "https://elsewhere.example.test/realms/learnstack"), IdentityRebindRefusal.NewRealmIssuerMismatch),
            ("no signing keys", FixedRealm.Reporting(proof.Subjects, keys: 0), IdentityRebindRefusal.NewRealmIncomplete),
            ("a partial enumeration", FixedRealm.Reporting(proof.Subjects, complete: false), IdentityRebindRefusal.NewRealmIncomplete),
        };
        foreach (var (why, realm, expected) in cases)
        {
            var admission = RebindProofSupport.Admit();
            var outcome = await proof.RunAsync(admission, realm);

            outcome.Refusal.Should().Be(expected, why);
            (await proof.IssuerAsync()).Should().Be(proof.OldIssuer, why);
            (await proof.AuditRowsAsync(admission)).Should().ContainSingle(why).Which.Operation.Should().Be("identity.authority.rebind_attempt");
        }
    }

    [Fact]
    public async Task An_Active_User_The_New_Realm_Does_Not_Report_Refuses_And_Names_Only_Counts()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 3);
        var admission = RebindProofSupport.Admit();

        var outcome = await proof.RunAsync(admission, FixedRealm.Reporting(proof.Subjects.Take(2)));

        outcome.Refusal.Should().Be(IdentityRebindRefusal.ContinuityBroken);
        outcome.Continuity!.MissingActive.Should().Be(1);
        outcome.ToString().Should().NotContain(proof.Subjects[2], "a refusal reports counts and never identifiers");
        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer);
        (await proof.AuditRowsAsync(admission)).Should().ContainSingle("only the intent row: no change, no result");
    }

    [Fact]
    public async Task A_Subject_That_Differs_In_Case_Or_By_Whitespace_Is_Not_The_Same_Subject()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        var changed = new[] { proof.Subjects[0].ToUpperInvariant(), " " + proof.Subjects[0] };

        var outcome = await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(changed));

        outcome.Refusal.Should().Be(IdentityRebindRefusal.ContinuityBroken, "the exact opaque sub is the identity");
    }

    [Fact]
    public async Task A_Closed_Persons_Key_Reappearing_Refuses_And_An_Absent_One_Does_Not()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 2);
        await proof.CloseAsync(proof.Users[1], closed: true);

        var reappears = await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(proof.Subjects));
        reappears.Refusal.Should().Be(IdentityRebindRefusal.ContinuityBroken, "a closed person must not reappear");
        reappears.Continuity.Should().BeEquivalentTo(new IdentityRebindContinuityResult(1, 0, 1, 2, 0, 0, 1, 0));
        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer);

        var absent = await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(proof.Subjects.Take(1)));
        absent.Result.Should().Be(IdentityRebindResult.Applied, "the tombstone's subject is simply not in the new realm");
        absent.Continuity!.StoredClosed.Should().Be(1);
    }

    [Fact]
    public async Task A_Closing_User_Retaining_Its_Subject_Is_Judged_By_Neither_Rule()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 2);
        await proof.CloseAsync(proof.Users[1], closed: false);

        var outcome = await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(proof.Subjects.Take(1)));

        outcome.Result.Should().Be(IdentityRebindResult.Applied);
        outcome.Continuity!.StoredClosing.Should().Be(1);
    }

    [Fact]
    public async Task A_Forced_Audit_Failure_Rolls_The_Issuer_Change_Back()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        var admission = RebindProofSupport.Admit();

        var outcome = await proof.RunAsync(admission, FixedRealm.Reporting(proof.Subjects), configure: store => store.FailResult = true);

        outcome.Refusal.Should().Be(IdentityRebindRefusal.AuditFailed);
        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer, "the issuer change and its audit row commit together or not at all");
        (await proof.AuditRowsAsync(admission)).Should().ContainSingle().Which.Operation.Should().Be("identity.authority.rebind_attempt");
    }

    [Fact]
    public async Task A_Run_Killed_Between_The_Intent_Row_And_The_Main_Transaction_Leaves_The_Intent_And_No_Change()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        var admission = RebindProofSupport.Admit();
        using var cancellation = new CancellationTokenSource();
        var realm = new FixedRealm(async (_, token) =>
        {
            await cancellation.CancelAsync();
            token.ThrowIfCancellationRequested();
            return null!;
        });

        var outcome = await proof.RunAsync(admission, realm, cancellation: cancellation.Token);

        outcome.Refusal.Should().Be(IdentityRebindRefusal.Cancelled);
        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer);
        (await proof.AuditRowsAsync(admission)).Should().ContainSingle().Which.Operation.Should().Be("identity.authority.rebind_attempt");
    }

    [Fact]
    public async Task A_Session_Killed_Before_Commit_Is_Reconciled_As_Not_Applied()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        var admission = RebindProofSupport.Admit();

        var outcome = await proof.RunAsync(admission, FixedRealm.Reporting(proof.Subjects), configure: store =>
            store.AfterResult = async connection =>
                await parent.Postgres.ExecuteAsSuperuserAsync(
                    $"SELECT pg_terminate_backend({((NpgsqlConnection)connection).ProcessID})"));

        outcome.Refusal.Should().Be(IdentityRebindRefusal.CommitFailed);
        outcome.Result.Should().Be(IdentityRebindResult.Refused);
        (await proof.IssuerAsync()).Should().Be(proof.OldIssuer, "the transaction died with its connection");
        (await proof.AuditRowsAsync(admission)).Should().ContainSingle("no result row either").Which.Operation
            .Should().Be("identity.authority.rebind_attempt");

        var retry = await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(proof.Subjects));
        retry.Result.Should().Be(IdentityRebindResult.Applied);
    }

    [Fact]
    public async Task A_Commit_Whose_Acknowledgement_Is_Lost_Is_Reconciled_By_Reading_The_Issuer()
    {
        await using var proof = await Proof.CreateAsync(parent, active: 1);
        await using var proxy = new CommitAckDroppingProxy(proof.RebindConnection);
        var admission = RebindProofSupport.Admit();

        var outcome = await proof.RunAsync(admission, FixedRealm.Reporting(proof.Subjects), connection: proxy.Through(proof.RebindConnection));

        proxy.Dropped.Should().BeTrue("the server received the COMMIT and its answer never arrived");
        outcome.Result.Should().Be(IdentityRebindResult.Applied, "the issuer read back is the target, so the commit applied");
        (await proof.IssuerAsync()).Should().Be(NewIssuer);
        (await proof.AuditRowsAsync(admission)).Select(row => row.Operation).Should().Equal(
            "identity.authority.rebind_attempt", "identity.authority.rebind");
    }

    [Fact]
    public async Task Runs_Are_Counted_By_Outcome_And_The_Continuity_Counts_By_Class()
    {
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var classes = new System.Collections.Concurrent.ConcurrentDictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == IdentityRebindMetrics.MeterName) l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (instrument.Name == IdentityRebindMetrics.RunCounterName && tag.Key == "outcome") outcomes.Add((string)tag.Value!);
                if (instrument.Name == IdentityRebindMetrics.ContinuityCounterName && tag.Key == "class")
                    classes.AddOrUpdate((string)tag.Value!, value, (_, current) => current + value);
            }
        });
        listener.Start();
        await using var proof = await Proof.CreateAsync(parent, active: 2);

        await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(proof.Subjects.Take(1)));
        await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(proof.Subjects));
        await proof.RunAsync(RebindProofSupport.Admit(), FixedRealm.Reporting(proof.Subjects));

        outcomes.Should().Contain(["refused_continuity_broken", "applied", "already_applied"]);
        classes.Should().ContainKey("missing_active").WhoseValue.Should().BeGreaterThanOrEqualTo(1);
        classes.Should().ContainKey("active");
    }

    private sealed record AuditRow(
        Guid Tenant, string Operation, string Outcome, Guid? Actor, string? Reason, string Entity,
        JsonElement Operator, JsonElement Before, JsonElement After, JsonElement Continuity, string Raw);

    private sealed record StoredUser(Guid Id, string KeyHex, string? Subject, string Status);

    private sealed class Proof : IAsyncDisposable
    {
        private readonly IdentityPublicReadFixture _database;
        private readonly PublicReadFixture _parent;

        private Proof(PublicReadFixture parent, IdentityPublicReadFixture database, UserId[] users, string[] subjects)
        {
            _parent = parent;
            _database = database;
            Users = users;
            Subjects = subjects;
        }

        internal string[] Subjects { get; }

        internal UserId[] Users { get; }

        internal Guid Authority => _database.Registration.AuthorityId.Value;

        internal string OldIssuer => _database.Registration.Issuer;

        internal string RebindConnection => _database.IdentityRebindConnectionString;

        internal string OwnerConnection => _database.MigrationConnectionString;

        internal string Database => RebindProofSupport.DatabaseOf(RebindConnection);

        internal static async Task<Proof> CreateAsync(PublicReadFixture parent, int active)
        {
            var users = Enumerable.Range(0, active).Select(_ => UserId.From(Guid.CreateVersion7())).ToArray();
            var subjects = users.Select(user => "rebind-proof-" + user.Value.ToString("N")).ToArray();
            var database = await IdentityPublicReadFixture.CreateAsync(
                parent, "https://old.example.test/realms/learnstack",
                users.Zip(subjects).ToDictionary(pair => pair.First, pair => pair.Second));
            return new Proof(parent, database, users, subjects);
        }

        internal async Task<IdentityRebindOutcome> RunAsync(
            PlatformAdmission admission,
            INewRealmContinuitySource realm,
            string? connection = null,
            IdentityAuthorityRegistration? target = null,
            TimeSpan? lockWait = null,
            Action<FaultingAuditStore>? configure = null,
            CancellationToken cancellation = default)
        {
            await using var scope = RebindProofSupport.Store();
            var store = new FaultingAuditStore(scope.ServiceProvider.GetRequiredService<IAuditStore>());
            configure?.Invoke(store);
            return await new IdentityIssuerRebind(store, new SystemClock()).RunAsync(
                connection ?? RebindConnection,
                target ?? new IdentityAuthorityRegistration(_database.Registration.AuthorityId, NewIssuer),
                admission, realm, lockWait, cancellation);
        }

        internal async Task<string> IssuerAsync() => await ReadAsync<string>("SELECT issuer FROM public.identity_authority");

        internal async Task<Guid> AuthorityAsync() => await ReadAsync<Guid>("SELECT authority_id FROM public.identity_authority");

        private async Task<T> ReadAsync<T>(string sql)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.AppConnectionString);
            await using var command = new NpgsqlCommand(sql, connection);
            return (T)(await command.ExecuteScalarAsync())!;
        }

        internal async Task<List<StoredUser>> UsersAsync()
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(OwnerConnection);
            await using var command = new NpgsqlCommand(
                "SELECT id, encode(subject_key, 'hex'), provider_subject, status FROM public.users ORDER BY id", connection);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<StoredUser>();
            while (await reader.ReadAsync())
            {
                rows.Add(new StoredUser(reader.GetGuid(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
            }

            return rows;
        }

        internal async Task<List<AuditRow>> AuditRowsAsync(PlatformAdmission admission)
        {
            await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.PlatformConnectionString);
            await using var command = new NpgsqlCommand(
                """
                SELECT tenant_id, operation, outcome, actor_user_id, reason, entity_id, metadata::text,
                       COALESCE(before_state::text, 'null'), COALESCE(after_state::text, 'null')
                FROM public.audit_log WHERE correlation_id = @correlation ORDER BY timestamp, operation
                """, connection);
            command.Parameters.AddWithValue("correlation", admission.Operator.CorrelationId!);
            await using var reader = await command.ExecuteReaderAsync();
            var rows = new List<AuditRow>();
            while (await reader.ReadAsync())
            {
                var metadata = JsonDocument.Parse(reader.GetString(6)).RootElement.Clone();
                rows.Add(new AuditRow(reader.GetGuid(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetGuid(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetString(5), metadata.GetProperty("operator"),
                    JsonDocument.Parse(reader.GetString(7)).RootElement.Clone(),
                    JsonDocument.Parse(reader.GetString(8)).RootElement.Clone(),
                    metadata.TryGetProperty("continuity", out var continuity) ? continuity : default,
                    reader.GetString(6) + reader.GetString(7) + reader.GetString(8)));
            }

            return rows;
        }

        /// <summary>A competing rebind: the exclusive lock held by a rebind-role transaction.</summary>
        internal async Task<IAsyncDisposable> HoldRebindLockAsync()
        {
            var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(RebindConnection);
            var transaction = await connection.BeginTransactionAsync();
            await IdentityMembershipFixture.ExecuteAsync(connection, transaction, "SELECT pg_catalog.pg_advisory_xact_lock(@key)",
                ("key", IdentityAdvisoryLockKey.ForAuthorityRebind()));
            return new Held(connection, transaction);
        }

        internal async Task<IAsyncDisposable> OpenSessionAsync(string role)
        {
            var template = role switch
            {
                "learnstack_app" => _database.AppConnectionString,
                "learnstack_outbox_admin" => _database.OutboxConnectionString,
                "learnstack_platform" => _database.PlatformConnectionString,
                _ => RebindProofSupport.In(IngressConnection, Database),
            };
            // Unpooled: a disposed pooled connection stays a live session of its role, which is what the freeze sees.
            return new Session(await PostgresFixture.OpenAsync(
                new NpgsqlConnectionStringBuilder(template) { Pooling = false }.ConnectionString));
        }

        private string IngressConnection => _parent.Postgres.IdentityIngressConnectionString;

        /// <summary>The fixture's one corruption: a user closed (or closing) the way closure will, then the controls restored.</summary>
        internal async Task CloseAsync(UserId user, bool closed)
        {
            await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(OwnerConnection);
            var check = await IdentityMembershipFixture.ScalarAsync<string>(owner, null,
                "SELECT pg_get_expr(polwithcheck,polrelid) FROM pg_policy WHERE polrelid='public.users'::regclass AND polname='users_isolation'");
            await using var transaction = await owner.BeginTransactionAsync();
            var status = closed ? "Closed" : "Closing";
            var subject = closed ? ", provider_subject=NULL" : string.Empty;
            await IdentityMembershipFixture.ExecuteAsync(owner, transaction, $"""
                ALTER TABLE public.users DISABLE TRIGGER tg_users_no_update;
                ALTER POLICY users_isolation ON public.users WITH CHECK (({check}) OR
                    (current_user='learnstack_migration' AND id='{user.Value:D}'::uuid AND status='{status}'
                     AND deleted_at IS NULL AND deleted_by IS NULL));
                UPDATE public.users SET status='{status}'{subject} WHERE id='{user.Value:D}'::uuid;
                ALTER POLICY users_isolation ON public.users WITH CHECK ({check});
                ALTER TABLE public.users ENABLE TRIGGER tg_users_no_update;
                """);
            await transaction.CommitAsync();
        }

        public ValueTask DisposeAsync() => _database.DisposeAsync();

        private sealed class Held(NpgsqlConnection connection, NpgsqlTransaction transaction) : IAsyncDisposable
        {
            public async ValueTask DisposeAsync()
            {
                await transaction.RollbackAsync();
                await transaction.DisposeAsync();
                await connection.DisposeAsync();
            }
        }

        private sealed class Session(DbConnection connection) : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => connection.DisposeAsync();
        }
    }
}
