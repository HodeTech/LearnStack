using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.MultiTenancy;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Federation;
using LearnStack.Modules.Tenancy.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.FederationConnectionSchemaSeed;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The operator's federation report, remove and disable against a real database and the real platform scope (ADR-0099
/// Amendment 10 and 11): the rows are read as <c>learnstack_platform</c> through its four granted columns, the realm is
/// a scripted provider, and every act's intent and completion rows are read back as written.
/// </summary>
/// <remarks>
/// The database is an isolated one that holds a tombstone and a live connection of tenant A and a live connection of
/// tenant B, as the shared schema does. A tenant can hold only one live connection, so no test writes a connection row,
/// and every act writes audit rows under tenant A or B: audit_log is append-only, so those rows stay out of the shared
/// schema, whose isolation census counts what each seeded tenant sees.
/// </remarks>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class FederationBrokerOperationsTests(FederationBrokerOperationsTests.FederationDatabase database)
    : IClassFixture<FederationBrokerOperationsTests.FederationDatabase>
{
    /// <summary>One isolated, fully migrated database with tenants A and B and the federation rows of the shared seed.</summary>
    public sealed class FederationDatabase(SchemaFixture schema) : IAsyncLifetime
    {
        private DisposableSchemaDatabase? _database;

        /// <summary>A tenant with no connection row, for the case where a live row appears after the intent.</summary>
        public static readonly Guid TenantC = Guid.Parse("33333333-3333-7333-8333-333333333333");

        public string PlatformConnectionString => _database!.PlatformConnectionString;

        public string AppConnectionString => _database!.AppConnectionString;

        public async Task InitializeAsync()
        {
            _database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
            await using (var owner = await PostgresFixture.OpenAsync(_database.MigrationConnectionString))
            {
                foreach (var (tenant, slug) in new[] { (SchemaFixture.TenantA, "alpha"), (SchemaFixture.TenantB, "beta"), (FederationDatabase.TenantC, "gamma") })
                {
                    await using var transaction = await owner.BeginTransactionAsync();
                    await SchemaQueries.ExecuteAsync(owner, transaction, $"SET LOCAL app.tenant_id = '{tenant:D}'");
                    await SchemaQueries.ExecuteAsync(
                        owner, transaction,
                        "INSERT INTO tenants (id, slug, display_name, status, created_at, created_by, row_version) "
                        + "VALUES (@id, @slug, @slug, 'Trial', now(), @actor, 0)",
                        ("id", tenant), ("slug", slug), ("actor", SchemaFixture.Actor));
                    await transaction.CommitAsync();
                }
            }

            await FederationConnectionSchemaSeed.SeedAsync(_database.AppConnectionString);
        }

        public async Task DisposeAsync()
        {
            if (_database is not null) await _database.DisposeAsync();
        }
    }

    private static FederationBrokerOwner OwnerOf(Guid connection, Guid? tenant = null) =>
        new(TenantId.From(tenant ?? SchemaFixture.TenantA), connection, Alias(connection));

    private static FederationBrokerFacts Facts(FederationBrokerOwner owner, bool readable = true, bool conforms = true, bool enabled = true) =>
        new(owner, readable, conforms, enabled, readable ? FederationBrokerProtocol.Oidc : null);

    [Fact]
    public async Task A_report_names_a_broker_with_no_row_a_tombstone_and_an_unreadable_one_and_not_a_healthy_live_one()
    {
        var operations = new Scripted();
        var healthy = OwnerOf(LiveA);
        var tombstoned = OwnerOf(TombstoneA);
        var absent = OwnerOf(Guid.CreateVersion7());
        var unreadableLive = OwnerOf(LiveB, SchemaFixture.TenantB);
        operations.Listed = [Facts(healthy), Facts(tombstoned), Facts(absent), Facts(unreadableLive, readable: false)];
        var service = Service(operations);

        var result = await service.ReportAsync(Admission(PlatformOperations.FederationReport), null, 20);

        result.Outcome.Should().Be(FederationReportOutcome.Ok);
        result.Items.Select(item => (item.ConnectionId, string.Join('+', item.Classes), item.RowState)).Should().Equal(
            (TombstoneA, "orphan", "deleted"),
            (absent.ConnectionId, "orphan", "absent"),
            (LiveB, "unreadable_owned", "Draft"));
        result.Items.Should().OnlyContain(item => item.Alias.StartsWith("lsf-", StringComparison.Ordinal));
        result.Scanned.Should().Be(4);
        // A healthy live broker with no mapper drift is no finding, and its mapper count was asked once.
        operations.MapperCalls.Should().Equal(healthy.Alias);
        result.Unread.Should().Be(0, "a tombstone, an absent row and an unreadable broker are never mapper-read, so none is unread");
    }

    [Fact]
    public async Task A_report_enters_the_scope_first_reads_the_realm_next_and_the_rows_only_after_it()
    {
        // A tenant commits its row before it creates the broker, so a broker the realm listed already has its row when the
        // rows are read; a reader that took the rows first would call a broker created in between an orphan. The order
        // is observed, not inferred: the scope entry, the listing and the row query are recorded as they happen.
        var events = new List<string>();
        var operations = new Scripted { Listed = [Facts(OwnerOf(LiveA))], Events = events };
        var service = new FederationBrokerOperations(new RecordingScope(Scope(false, false), events), operations);

        await service.ReportAsync(Admission(PlatformOperations.FederationReport), null, 20);

        events.Should().Equal("enter", "list", "query", "mappers");
    }

    [Fact]
    public async Task A_report_never_names_a_broker_whose_marker_and_row_disagree_about_the_tenant()
    {
        var operations = new Scripted();
        var wrongTenant = OwnerOf(LiveA, SchemaFixture.TenantB);
        operations.Listed = [Facts(wrongTenant, readable: false)];

        var result = await Service(operations).ReportAsync(Admission(PlatformOperations.FederationReport), null, 20);

        result.Items.Should().BeEmpty();
        result.Ignored.Should().Be(1, "it is counted and never named");
    }

    [Fact]
    public async Task A_report_pages_by_an_opaque_cursor_it_issued_and_refuses_one_it_did_not()
    {
        var operations = new Scripted { Listed = [.. Enumerable.Range(0, 5).Select(_ => Facts(OwnerOf(Guid.CreateVersion7()), readable: false))] };
        var service = Service(operations);

        var first = await service.ReportAsync(Admission(PlatformOperations.FederationReport), null, 2);
        var second = await service.ReportAsync(Admission(PlatformOperations.FederationReport), first.NextCursor, 2);
        var last = await service.ReportAsync(Admission(PlatformOperations.FederationReport), second.NextCursor, 2);

        (first.Scanned, second.Scanned, last.Scanned).Should().Be((2, 2, 1));
        last.NextCursor.Should().BeNull();
        first.Items.Concat(second.Items).Concat(last.Items).Select(item => item.Alias).Should().OnlyHaveUniqueItems().And.HaveCount(5);
        operations.Offsets.Should().Equal(0, 2, 4);
        foreach (var forged in new[] { "x", "djF-ZmVkZXJhdGlvbn4tMQ", Convert.ToBase64String("v1~federation~-1"u8) })
        {
            var refused = await service.ReportAsync(Admission(PlatformOperations.FederationReport), forged, 2);
            refused.Outcome.Should().Be(FederationReportOutcome.InvalidCursor, forged);
        }

        operations.Offsets.Should().Equal([0, 2, 4], "a refused cursor reaches no provider and enters no scope");
    }

    [Fact]
    public async Task A_report_the_provider_cannot_answer_is_unavailable_and_writes_only_the_scopes_entry_row()
    {
        var operations = new Scripted { Unavailable = true };
        var admission = Admission(PlatformOperations.FederationReport, ticket: "CHG-1");

        var result = await Service(operations).ReportAsync(admission, null, 20);

        result.Outcome.Should().Be(FederationReportOutcome.Unavailable);
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
        rows[0].Metadata.Should().Contain("CHG-1", "the entry row is a sentinel row, which carries the ticket");
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(false, true, true)]
    public async Task A_remove_of_a_broker_that_reads_or_is_still_enabled_with_a_live_row_is_refused_before_anything_is_written_or_sent(
        bool readable, bool conforms, bool enabled)
    {
        // A pair that reads is retired by the tenant's own Delete, and an unreadable broker that is still enabled may be
        // serving: the operator disables it first (ADR-0099 Amendment 13 item 1).
        var operations = new Scripted { Inspected = Facts(OwnerOf(LiveA), readable, conforms, enabled), InspectedMappers = conforms ? 0 : 1 };
        var admission = Admission(PlatformOperations.FederationRemove);

        var result = await Service(operations).RemoveAsync(admission, Alias(LiveA));

        result.Outcome.Should().Be(FederationActOutcome.LiveConnection);
        operations.Removed.Should().BeEmpty();
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
    }

    [Fact]
    public async Task A_remove_of_an_unreadable_disabled_broker_is_applied_even_with_a_live_row_because_the_tenants_delete_cannot_reach_it()
    {
        // The tenant's Delete reports an unreadable owned broker as foreign and fails, so a live row would otherwise hold it
        // forever (ADR-0099 Amendment 12). It is disabled first, and the adapter proves that again at the delete.
        var owner = OwnerOf(LiveA);
        var operations = new Scripted { Inspected = Facts(owner, readable: false, enabled: false) };
        var admission = Admission(PlatformOperations.FederationRemove);

        var result = await Service(operations).RemoveAsync(admission, owner.Alias);

        result.Should().Be(new FederationActResult(FederationActOutcome.Applied, SchemaFixture.TenantA, LiveA));
        operations.Removed.Should().ContainSingle();
        operations.RemoveRequirements.Should().Equal([true], "the adapter is told to prove 'unreadable and disabled' at the delete");
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, "platform.federation.remove_attempt", "platform.federation.remove");
        using var before = JsonDocument.Parse(rows[1].Before);
        before.RootElement.GetProperty("rowState").GetString().Should().Be("Draft", "the live row is recorded as found");
        before.RootElement.GetProperty("classes").EnumerateArray().Select(item => item.GetString()).Should().Equal("unreadable_owned");
        (await ScalarAsync("SELECT count(*) FROM public.federation_connections WHERE id = @id AND state <> 'Deleted'", LiveA))
            .Should().Be(1, "the operator never writes the tenant's row");
    }

    [Fact]
    public async Task A_remove_whose_broker_stopped_being_unreadable_and_disabled_before_the_delete_is_refused_by_the_provider_proof()
    {
        var owner = OwnerOf(LiveA);
        var operations = new Scripted { Inspected = Facts(owner, readable: false, enabled: false), RemoveOutcome = FederationOperatorOutcome.Refused };
        var admission = Admission(PlatformOperations.FederationRemove);

        var result = await Service(operations).RemoveAsync(admission, owner.Alias);

        result.Outcome.Should().Be(FederationActOutcome.Refused);
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        using var after = JsonDocument.Parse(rows[2].After);
        after.RootElement.GetProperty("outcome").GetString().Should().Be("refused");
        after.RootElement.GetProperty("accountReviewOwed").GetBoolean().Should().BeFalse("nothing was deleted");
    }

    [Fact]
    public async Task A_remove_of_an_orphan_does_not_ask_the_adapter_for_the_unreadable_proof()
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        var operations = new Scripted { Inspected = Facts(owner, readable: true, conforms: false), InspectedMappers = 1 };

        await Service(operations).RemoveAsync(Admission(PlatformOperations.FederationRemove), owner.Alias);

        operations.RemoveRequirements.Should().Equal([false], "an orphan has no live row to be let through");
    }

    [Theory]
    [InlineData("tombstone")]
    [InlineData("absent")]
    [InlineData("tenant_without_a_row")]
    public async Task A_remove_of_an_orphan_writes_an_intent_before_the_provider_call_and_a_completion_after_it_under_the_markers_tenant(string kind)
    {
        var tenant = kind == "tenant_without_a_row" ? Guid.CreateVersion7() : SchemaFixture.TenantA;
        var connection = kind == "tombstone" ? TombstoneA : Guid.CreateVersion7();
        var owner = OwnerOf(connection, tenant);
        var operations = new Scripted { Inspected = Facts(owner, readable: false), InspectedMappers = 0 };
        var admission = Admission(PlatformOperations.FederationRemove, ticket: "CHG-5");
        operations.BeforeRemove = async () =>
        {
            // The intent row is already committed when the provider is called: another connection sees it.
            var rows = await RowsAsync(admission.Operator.CorrelationId!);
            rows.Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation, "platform.federation.remove_attempt");
        };

        var result = await Service(operations).RemoveAsync(admission, owner.Alias);

        result.Should().Be(new FederationActResult(FederationActOutcome.Applied, tenant, connection));
        operations.Removed.Should().ContainSingle().Which.Should().Be(owner);
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Select(row => (row.Operation, row.Outcome)).Should().Equal(
            (PlatformAdminScope.EnterOperation, "success"),
            ("platform.federation.remove_attempt", "indeterminate"),
            ("platform.federation.remove", "success"));
        rows.Skip(1).Should().OnlyContain(row => row.Tenant == tenant, "the rows name the marker's tenant, never the sentinel");
        rows.Skip(1).Should().OnlyContain(row => !row.Metadata.Contains("CHG-5"), "a tenant can read these rows");
        rows[1].EntityId.Should().Be(connection.ToString("D"));
        using var after = JsonDocument.Parse(rows[2].After);
        after.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
        after.RootElement.GetProperty("accountReviewOwed").GetBoolean().Should().BeTrue();
        using var before = JsonDocument.Parse(rows[1].Before);
        before.RootElement.GetProperty("classes").EnumerateArray().Select(item => item.GetString()).Should().Contain("orphan");
    }

    [Fact]
    public async Task A_remove_of_an_absent_or_foreign_broker_writes_no_tenant_row()
    {
        var operations = new Scripted { InspectedStatus = FederationInspectionStatus.Absent };
        var absent = Admission(PlatformOperations.FederationRemove);
        (await Service(operations).RemoveAsync(absent, Alias(Guid.CreateVersion7()))).Outcome.Should().Be(FederationActOutcome.AlreadyApplied);

        operations.InspectedStatus = FederationInspectionStatus.NotOwned;
        var foreign = Admission(PlatformOperations.FederationRemove);
        (await Service(operations).RemoveAsync(foreign, Alias(Guid.CreateVersion7()))).Outcome.Should().Be(FederationActOutcome.NotOwned);

        operations.Removed.Should().BeEmpty();
        foreach (var admission in new[] { absent, foreign })
            (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
    }

    [Theory]
    [InlineData(FederationOperatorOutcome.Uncertain, "uncertain", FederationActOutcome.Uncertain)]
    [InlineData(FederationOperatorOutcome.Unavailable, "unavailable", FederationActOutcome.Unavailable)]
    [InlineData(FederationOperatorOutcome.Refused, "refused", FederationActOutcome.Refused)]
    public async Task A_provider_that_does_not_apply_a_remove_is_recorded_in_the_completion_row(
        FederationOperatorOutcome provider, string recorded, FederationActOutcome expected)
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        var operations = new Scripted { Inspected = Facts(owner, readable: false), RemoveOutcome = provider };
        var admission = Admission(PlatformOperations.FederationRemove);

        var result = await Service(operations).RemoveAsync(admission, owner.Alias);

        result.Outcome.Should().Be(expected);
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, "platform.federation.remove_attempt", "platform.federation.remove");
        using var after = JsonDocument.Parse(rows[2].After);
        after.RootElement.GetProperty("outcome").GetString().Should().Be(recorded);
    }

    [Fact]
    public async Task A_failed_intent_write_stops_the_remove_before_the_provider_is_called()
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        var operations = new Scripted { Inspected = Facts(owner, readable: false) };
        var admission = Admission(PlatformOperations.FederationRemove);

        var act = async () => await Service(operations, failIntent: true).RemoveAsync(admission, owner.Alias);

        await act.Should().ThrowAsync<AuditWriteFailedException>();
        operations.Removed.Should().BeEmpty("the effect must not start on an intent nobody recorded");
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
    }

    [Theory]
    [InlineData("remove", FederationOperatorOutcome.Applied)]
    [InlineData("remove", FederationOperatorOutcome.Uncertain)]
    [InlineData("disable", FederationOperatorOutcome.Applied)]
    public async Task A_completion_that_cannot_be_written_after_a_change_is_answered_as_the_unknown_outcome_and_leaves_the_intent_standing(
        string act, FederationOperatorOutcome provider)
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        var operations = new Scripted { Inspected = Facts(owner, readable: false), RemoveOutcome = provider, DisableOutcome = provider };
        var remove = act == "remove";
        var admission = Admission(remove ? PlatformOperations.FederationRemove : PlatformOperations.FederationDisable);
        var service = Service(operations, failResult: true);

        var result = remove ? await service.RemoveAsync(admission, owner.Alias) : await service.DisableAsync(admission, owner.Alias);

        result.Outcome.Should().Be(FederationActOutcome.Uncertain, "the change was sent and its record was not written");
        (remove ? operations.Removed : operations.Disabled).Should().ContainSingle("the provider call had been made");
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, remove ? "platform.federation.remove_attempt" : "platform.federation.disable_attempt");
    }

    [Fact]
    public async Task A_completion_that_cannot_be_written_when_nothing_changed_is_still_the_audit_failure()
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        var operations = new Scripted { Inspected = Facts(owner, readable: false), RemoveOutcome = FederationOperatorOutcome.Refused };
        var admission = Admission(PlatformOperations.FederationRemove);

        var act = async () => await Service(operations, failResult: true).RemoveAsync(admission, owner.Alias);

        await act.Should().ThrowAsync<AuditWriteFailedException>("a refused change recorded nowhere is safe to repeat");
    }

    [Fact]
    public async Task A_caller_that_leaves_while_the_provider_changes_the_broker_is_recorded_as_uncertain_on_a_detached_budget()
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        using var caller = new CancellationTokenSource();
        var operations = new Scripted { Inspected = Facts(owner, readable: false) };
        operations.BeforeRemove = () =>
        {
            // The request is aborted after the change may have been sent: the adapter rethrows the caller's cancellation.
            caller.Cancel();
            throw new OperationCanceledException(caller.Token);
        };
        var admission = Admission(PlatformOperations.FederationRemove);

        var result = await Service(operations).RemoveAsync(admission, owner.Alias, caller.Token);

        result.Outcome.Should().Be(FederationActOutcome.Uncertain);
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, "platform.federation.remove_attempt", "platform.federation.remove");
        using var after = JsonDocument.Parse(rows[2].After);
        after.RootElement.GetProperty("outcome").GetString().Should().Be("uncertain");
        after.RootElement.GetProperty("accountReviewOwed").GetBoolean().Should().BeTrue("the change may have deleted the links");
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("disable")]
    public async Task A_marker_that_names_another_tenant_than_its_row_is_not_owned_and_writes_no_tenant_row(string act)
    {
        // The tombstone is tenant A's, and the instance's marker names tenant B: never touched, and no row names B.
        var owner = OwnerOf(TombstoneA, SchemaFixture.TenantB);
        var operations = new Scripted { Inspected = Facts(owner, readable: false) };
        var remove = act == "remove";
        var admission = Admission(remove ? PlatformOperations.FederationRemove : PlatformOperations.FederationDisable);
        var service = Service(operations);

        var result = remove ? await service.RemoveAsync(admission, owner.Alias) : await service.DisableAsync(admission, owner.Alias);

        result.Outcome.Should().Be(FederationActOutcome.NotOwned);
        operations.Removed.Should().BeEmpty();
        operations.Disabled.Should().BeEmpty();
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
    }

    [Fact]
    public async Task A_live_row_that_appears_after_the_intent_refuses_the_delete_and_is_recorded_without_calling_the_provider()
    {
        // Amendment 10 item 3: the row is read again immediately before the delete. A tenant commits its row at that moment.
        var connection = Guid.CreateVersion7();
        var owner = OwnerOf(connection, FederationDatabase.TenantC);
        var operations = new Scripted { Inspected = Facts(owner, readable: true, conforms: false), InspectedMappers = 1 };
        var admission = Admission(PlatformOperations.FederationRemove);
        var service = new FederationBrokerOperations(Scope(false, false, afterIntent: async _ =>
        {
            await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(database.AppConnectionString);
            await using var transaction = await app.BeginTransactionAsync();
            await FenceAsync(app, transaction, FederationDatabase.TenantC);
            await InsertAsync(app, transaction, connection, FederationDatabase.TenantC, IdentityMembershipFixture.AdminA,
                DateTimeOffset.UtcNow.AddMinutes(-5));
            await transaction.CommitAsync();
        }), operations, TimeSpan.FromSeconds(30));

        var result = await service.RemoveAsync(admission, owner.Alias);

        result.Should().Be(new FederationActResult(FederationActOutcome.LiveConnection, FederationDatabase.TenantC, connection));
        operations.Removed.Should().BeEmpty("the provider is not called once the row is live");
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, "platform.federation.remove_attempt", "platform.federation.remove");
        using var before = JsonDocument.Parse(rows[1].Before);
        before.RootElement.GetProperty("rowState").GetString().Should().Be("absent", "the first read found no row");
        using var after = JsonDocument.Parse(rows[2].After);
        (after.RootElement.GetProperty("outcome").GetString(), after.RootElement.GetProperty("cause").GetString())
            .Should().Be(("refused", "live_connection"));
        after.RootElement.GetProperty("accountReviewOwed").GetBoolean().Should().BeFalse("nothing was changed");
    }

    [Theory]
    [InlineData("io")]
    [InlineData("closed")]
    [InlineData("timeout")]
    public async Task A_commit_that_fails_after_a_change_answers_the_unknown_outcome_whatever_the_failure_and_keeps_its_cause(string kind)
    {
        Exception fault = kind switch
        {
            "io" => new NpgsqlException("the connection dropped", new IOException("reset")),
            "closed" => new InvalidOperationException("The transaction has completed"),
            _ => new TimeoutException("the commit timed out"),
        };
        var owner = OwnerOf(Guid.CreateVersion7());
        var operations = new Scripted { Inspected = Facts(owner, readable: false) };
        var admission = Admission(PlatformOperations.FederationRemove);
        var service = new FederationBrokerOperations(
            new FaultingScope(Scope(false, false), new HandleFaults { Commit = fault }), operations, TimeSpan.FromSeconds(30));

        var result = await service.RemoveAsync(admission, owner.Alias);

        result.Outcome.Should().Be(FederationActOutcome.Uncertain, "the change was sent and its record was not committed");
        result.Cause.Should().BeSameAs(fault, "the cause goes to the route's log");
        operations.Removed.Should().ContainSingle();
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, "platform.federation.remove_attempt");
    }

    [Fact]
    public async Task A_completion_write_that_outlives_its_budget_is_cut_and_answers_the_unknown_outcome()
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        var operations = new Scripted { Inspected = Facts(owner, readable: false) };
        var admission = Admission(PlatformOperations.FederationRemove);
        var service = new FederationBrokerOperations(
            new FaultingScope(Scope(false, false), new HandleFaults { HangResult = true }), operations, TimeSpan.FromMilliseconds(200));

        var result = await service.RemoveAsync(admission, owner.Alias);

        result.Outcome.Should().Be(FederationActOutcome.Uncertain);
        result.Cause.Should().BeAssignableTo<OperationCanceledException>("the budget, not the request, cut the write");
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, "platform.federation.remove_attempt");
    }

    [Fact]
    public async Task A_failure_while_the_handle_is_disposed_after_a_change_is_the_unknown_outcome_and_never_nothing_changed()
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        var fault = new InvalidOperationException("the connection could not be returned");
        var operations = new Scripted { Inspected = Facts(owner, readable: false) };
        var service = new FederationBrokerOperations(
            new FaultingScope(Scope(false, false), new HandleFaults { Dispose = fault }), operations, TimeSpan.FromSeconds(30));

        var act = async () => await service.RemoveAsync(Admission(PlatformOperations.FederationRemove), owner.Alias);

        var thrown = (await act.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which;
        (thrown.CommitOutcomeUnknown, thrown.InnerException).Should().Be((true, fault));
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("disable")]
    public async Task An_unexpected_provider_failure_after_the_change_may_have_been_sent_is_completed_as_uncertain_with_its_cause(string act)
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        var fault = new ObjectDisposedException("the host is shutting down");
        var operations = new Scripted { Inspected = Facts(owner, readable: false) };
        operations.BeforeRemove = () => throw fault;
        operations.BeforeDisable = () => throw fault;
        var remove = act == "remove";
        var admission = Admission(remove ? PlatformOperations.FederationRemove : PlatformOperations.FederationDisable);
        var service = Service(operations);

        var result = remove ? await service.RemoveAsync(admission, owner.Alias) : await service.DisableAsync(admission, owner.Alias);

        result.Outcome.Should().Be(FederationActOutcome.Uncertain);
        result.Cause.Should().BeSameAs(fault);
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, remove ? "platform.federation.remove_attempt" : "platform.federation.disable_attempt",
            remove ? "platform.federation.remove" : "platform.federation.disable");
        using var after = JsonDocument.Parse(rows[2].After);
        after.RootElement.GetProperty("outcome").GetString().Should().Be("uncertain");
    }

    [Fact]
    public async Task A_caller_that_leaves_while_the_provider_disables_the_broker_is_recorded_as_uncertain()
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        using var caller = new CancellationTokenSource();
        var operations = new Scripted { Inspected = Facts(owner, readable: false) };
        operations.BeforeDisable = () =>
        {
            caller.Cancel();
            throw new OperationCanceledException(caller.Token);
        };
        var admission = Admission(PlatformOperations.FederationDisable);

        var result = await Service(operations).DisableAsync(admission, owner.Alias, caller.Token);

        result.Outcome.Should().Be(FederationActOutcome.Uncertain);
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, "platform.federation.disable_attempt", "platform.federation.disable");
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("disable")]
    public async Task A_broker_the_provider_finds_already_gone_or_disabled_after_the_intent_is_recorded_as_already_applied_with_no_review_owed(string act)
    {
        var owner = OwnerOf(Guid.CreateVersion7());
        var operations = new Scripted
        {
            Inspected = Facts(owner, readable: false),
            RemoveOutcome = FederationOperatorOutcome.AlreadyApplied,
            DisableOutcome = FederationOperatorOutcome.AlreadyApplied,
        };
        var remove = act == "remove";
        var admission = Admission(remove ? PlatformOperations.FederationRemove : PlatformOperations.FederationDisable);
        var service = Service(operations);

        var result = remove ? await service.RemoveAsync(admission, owner.Alias) : await service.DisableAsync(admission, owner.Alias);

        result.Outcome.Should().Be(FederationActOutcome.AlreadyApplied);
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Should().HaveCount(3, "it changed between the first read and the provider call, so both rows exist");
        using var after = JsonDocument.Parse(rows[2].After);
        (after.RootElement.GetProperty("outcome").GetString(), after.RootElement.GetProperty("accountReviewOwed").GetBoolean())
            .Should().Be(("already_applied", false));
    }

    [Fact]
    public async Task A_listing_that_runs_out_of_the_pages_time_is_unavailable_and_never_an_empty_realm()
    {
        var operations = new Scripted { ListFailure = new OperationCanceledException("the page's time ran out") };

        var result = await Service(operations).ReportAsync(Admission(PlatformOperations.FederationReport), null, 20);

        result.Outcome.Should().Be(FederationReportOutcome.Unavailable);
        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task An_intent_commit_that_fails_for_any_reason_stops_the_act_as_the_audit_failure_before_the_provider_is_called()
    {
        // The connection is closed after the intent row is written and before it is committed, as a dropped link would be:
        // the commit then fails with a failure that is not a database exception, and it must still be the audit failure.
        var owner = OwnerOf(Guid.CreateVersion7());
        var operations = new Scripted { Inspected = Facts(owner, readable: false) };
        var admission = Admission(PlatformOperations.FederationRemove);
        var service = new FederationBrokerOperations(
            Scope(false, false, afterIntent: async connection => await connection.CloseAsync()), operations, TimeSpan.FromSeconds(30));

        var act = async () => await service.RemoveAsync(admission, owner.Alias);

        await act.Should().ThrowAsync<AuditWriteFailedException>();
        operations.Removed.Should().BeEmpty("the effect must not start on an intent nobody can prove was recorded");
    }

    [Theory]
    [InlineData(FederationOperatorOutcome.Uncertain, "uncertain", FederationActOutcome.Uncertain)]
    [InlineData(FederationOperatorOutcome.Unavailable, "unavailable", FederationActOutcome.Unavailable)]
    [InlineData(FederationOperatorOutcome.Refused, "refused", FederationActOutcome.Refused)]
    [InlineData(FederationOperatorOutcome.NotOwned, "not_owned", FederationActOutcome.NotOwned)]
    public async Task A_provider_that_does_not_apply_a_disable_is_recorded_in_the_completion_row(
        FederationOperatorOutcome provider, string recorded, FederationActOutcome expected)
    {
        var owner = OwnerOf(LiveA);
        var operations = new Scripted { Inspected = Facts(owner, readable: false), DisableOutcome = provider };
        var admission = Admission(PlatformOperations.FederationDisable);

        var result = await Service(operations).DisableAsync(admission, owner.Alias);

        result.Outcome.Should().Be(expected);
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, "platform.federation.disable_attempt", "platform.federation.disable");
        using var after = JsonDocument.Parse(rows[2].After);
        after.RootElement.GetProperty("outcome").GetString().Should().Be(recorded);
    }

    [Theory]
    [InlineData(FederationInspectionStatus.Unavailable, FederationActOutcome.Unavailable)]
    [InlineData(FederationInspectionStatus.Absent, FederationActOutcome.NotFound)]
    [InlineData(FederationInspectionStatus.NotOwned, FederationActOutcome.NotOwned)]
    public async Task A_disable_the_provider_cannot_inspect_or_that_names_nothing_it_owns_sends_nothing_and_writes_no_tenant_row(
        FederationInspectionStatus status, FederationActOutcome expected)
    {
        var operations = new Scripted { InspectedStatus = status };
        var admission = Admission(PlatformOperations.FederationDisable);

        var result = await Service(operations).DisableAsync(admission, Alias(Guid.CreateVersion7()));

        result.Outcome.Should().Be(expected);
        operations.Disabled.Should().BeEmpty();
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
    }

    [Fact]
    public async Task A_provider_that_cannot_inspect_a_remove_sends_nothing_and_writes_no_tenant_row()
    {
        var operations = new Scripted { InspectedStatus = FederationInspectionStatus.Unavailable };
        var admission = Admission(PlatformOperations.FederationRemove);

        var result = await Service(operations).RemoveAsync(admission, Alias(Guid.CreateVersion7()));

        result.Outcome.Should().Be(FederationActOutcome.Unavailable);
        operations.Removed.Should().BeEmpty();
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
    }

    [Fact]
    public async Task A_report_counts_the_mappers_of_a_healthy_broker_and_names_one_that_carries_drift()
    {
        var healthy = OwnerOf(LiveA);
        var operations = new Scripted { Listed = [Facts(healthy)] };
        operations.MapperCounts[healthy.Alias] = 2;

        var result = await Service(operations).ReportAsync(Admission(PlatformOperations.FederationReport), null, 20);

        var item = result.Items.Should().ContainSingle().Which;
        (string.Join('+', item.Classes), item.Mappers, item.RowState).Should().Be(("nonconforming", 2, "Draft"));
        result.Unread.Should().Be(0);
    }

    [Fact]
    public async Task A_broker_whose_mappers_could_not_be_read_is_counted_as_unread_and_never_reported_as_clean()
    {
        var first = OwnerOf(LiveA);
        var second = OwnerOf(LiveB, SchemaFixture.TenantB);
        var operations = new Scripted { Listed = [Facts(first), Facts(second)] };
        operations.MapperCounts[first.Alias] = null;
        operations.MapperDeadlines.Add(second.Alias);

        var result = await Service(operations).ReportAsync(Admission(PlatformOperations.FederationReport), null, 20);

        result.Outcome.Should().Be(FederationReportOutcome.Ok);
        result.Items.Should().BeEmpty("a broker with no mapper count is unknown, and an unknown broker is not a finding");
        result.Unread.Should().Be(2, "the page says how many healthy-looking brokers it could not clear");
    }

    [Theory]
    [InlineData("report")]
    [InlineData("remove")]
    [InlineData("disable")]
    public async Task A_transient_database_fault_is_the_unavailable_owner_with_its_cause_and_never_a_raw_failure(string act)
    {
        var fault = new NpgsqlException("the database is not reachable", new IOException("reset"));
        var owner = OwnerOf(Guid.CreateVersion7());
        var service = new FederationBrokerOperations(new ThrowingScope(fault), new Scripted { Inspected = Facts(owner, readable: false) });

        var call = act switch
        {
            "report" => (Func<Task>)(async () => await service.ReportAsync(Admission(PlatformOperations.FederationReport), null, 5)),
            "remove" => async () => await service.RemoveAsync(Admission(PlatformOperations.FederationRemove), owner.Alias),
            _ => async () => await service.DisableAsync(Admission(PlatformOperations.FederationDisable), owner.Alias),
        };

        (await call.Should().ThrowAsync<OperatorRecoveryUnavailableException>()).Which.InnerException.Should().BeSameAs(fault);
    }

    [Fact]
    public async Task A_database_fault_that_is_not_transient_surfaces_as_the_defect_it_is()
    {
        var defect = new InvalidOperationException("a defect");
        var service = new FederationBrokerOperations(new ThrowingScope(defect), new Scripted());

        var report = async () => await service.ReportAsync(Admission(PlatformOperations.FederationReport), null, 5);

        (await report.Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(defect);
    }

    [Theory]
    [InlineData("healthy_live", FederationActOutcome.NotApplicable)]
    [InlineData("already_disabled", FederationActOutcome.AlreadyApplied)]
    public async Task A_disable_that_nothing_needs_writes_no_row_and_sends_nothing(string kind, FederationActOutcome expected)
    {
        var owner = OwnerOf(LiveA);
        var facts = kind == "healthy_live" ? Facts(owner) : Facts(owner, readable: false, enabled: false);
        var operations = new Scripted { Inspected = facts };
        var admission = Admission(PlatformOperations.FederationDisable);

        var result = await Service(operations).DisableAsync(admission, owner.Alias);

        result.Outcome.Should().Be(expected);
        operations.Disabled.Should().BeEmpty();
        (await RowsAsync(admission.Operator.CorrelationId!)).Select(row => row.Operation).Should().Equal(PlatformAdminScope.EnterOperation);
    }

    [Theory]
    [InlineData("unreadable", false, true, 0)]
    [InlineData("profile_drift", true, false, 0)]
    [InlineData("mapper_drift", true, true, 2)]
    public async Task A_disable_of_a_broker_the_tenant_cannot_reach_or_that_has_drifted_is_applied_with_two_rows(
        string kind, bool readable, bool conforms, int mappers)
    {
        var owner = OwnerOf(LiveA);
        var operations = new Scripted { Inspected = Facts(owner, readable, conforms), InspectedMappers = mappers };
        var admission = Admission(PlatformOperations.FederationDisable);

        var result = await Service(operations).DisableAsync(admission, owner.Alias);

        result.Outcome.Should().Be(FederationActOutcome.Applied, kind);
        operations.Disabled.Should().ContainSingle();
        var rows = await RowsAsync(admission.Operator.CorrelationId!);
        rows.Select(row => row.Operation).Should().Equal(
            PlatformAdminScope.EnterOperation, "platform.federation.disable_attempt", "platform.federation.disable");
        using var before = JsonDocument.Parse(rows[1].Before);
        before.RootElement.GetProperty("classes").EnumerateArray().Select(item => item.GetString()).Should().Contain(
            readable ? "nonconforming" : "unreadable_owned");
        before.RootElement.GetProperty("rowState").GetString().Should().Be("Draft", "the tenant's own row is left as found");
    }

    [Fact]
    public async Task An_admission_for_another_operation_is_refused_by_each_entry_before_the_provider_or_the_scope()
    {
        var operations = new Scripted();
        var service = Service(operations);
        var wrong = Admission(PlatformOperations.FederationReport);

        var remove = async () => await service.RemoveAsync(wrong, Alias(Guid.CreateVersion7()));
        var disable = async () => await service.DisableAsync(wrong, Alias(Guid.CreateVersion7()));
        var report = async () => await service.ReportAsync(Admission(PlatformOperations.FederationRemove), null, 5);

        await remove.Should().ThrowAsync<ArgumentException>();
        await disable.Should().ThrowAsync<ArgumentException>();
        await report.Should().ThrowAsync<ArgumentException>();
        operations.Calls.Should().BeEmpty();
        (await RowsAsync(wrong.Operator.CorrelationId!)).Should().BeEmpty("no scope was entered");
    }

    [Fact]
    public async Task The_platform_role_reads_exactly_the_four_projection_columns_and_writes_nothing()
    {
        await using var platform = await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT attname FROM pg_attribute
            WHERE attrelid = 'public.federation_connections'::regclass AND attnum > 0 AND NOT attisdropped
                AND has_column_privilege('learnstack_platform', 'public.federation_connections', attnum, 'SELECT')
            ORDER BY attname
            """, (NpgsqlConnection)platform);
        var granted = new List<string>();
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) granted.Add(reader.GetString(0));
        granted.Should().Equal("deleted_at", "id", "state", "tenant_id");

        foreach (var sql in new[]
                 {
                     "SELECT alias FROM public.federation_connections", "SELECT display_name FROM public.federation_connections",
                     "SELECT issuer_or_entity_id FROM public.federation_connections", "SELECT * FROM public.federation_connections",
                     "SELECT client_id FROM public.federation_connections", "SELECT configuration_digest FROM public.federation_connections",
                     "UPDATE public.federation_connections SET state = 'Deleted'", "DELETE FROM public.federation_connections",
                     "INSERT INTO public.federation_connections (id) VALUES (gen_random_uuid())",
                 })
        {
            await FluentActions.Awaiting(() => SchemaQueries.ExecuteAsync(platform, null, sql)).Should()
                .ThrowAsync<PostgresException>(sql).Where(failure => failure.SqlState == PostgresErrorCodes.InsufficientPrivilege, sql);
        }

        // The granted four read across both tenants, which the role's BYPASSRLS makes possible.
        await using var distinct = new NpgsqlCommand(
            "SELECT count(DISTINCT tenant_id) FROM public.federation_connections", (NpgsqlConnection)platform);
        ((long)(await distinct.ExecuteScalarAsync())!).Should().BeGreaterThanOrEqualTo(2);
    }

    // A generous budget keeps the cases that finish well inside it from depending on a stalled runner.
    private FederationBrokerOperations Service(Scripted operations, bool failIntent = false, bool failResult = false, TimeSpan? budget = null) =>
        new(Scope(failIntent, failResult), operations, budget ?? TimeSpan.FromSeconds(30));

    private PlatformAdminScope Scope(bool failIntent, bool failResult, Func<System.Data.Common.DbConnection, Task>? afterIntent = null)
    {
        var platform = LearnStack.Api.Composition.PersistenceCompositionExtensions.BuildPlatformDataSource(database.PlatformConnectionString);
        var store = AuditStore();
        return new PlatformAdminScope(
            new PrivilegedDataSource(() => platform), new AuditCatalog([new TenancyAuditCatalogSource()]),
            failIntent || failResult || afterIntent is not null ? new FailingStore(store, failIntent, failResult, afterIntent) : store,
            new SystemClock(), new SystemGuidFactory(), NullLogger<PlatformAdminScope>.Instance);
    }

    private static PlatformAdmission Admission(PlatformOperation operation, string? ticket = null) => TestPlatformAdmissions.Admission(
        operation, ticket, correlationId: "00-federation-" + Guid.NewGuid().ToString("N"));

    private async Task<long> ScalarAsync(string sql, Guid id)
    {
        await using var connection = await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var command = new NpgsqlCommand(sql, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("id", id);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<List<AuditRow>> RowsAsync(string correlation)
    {
        await using var connection = await PostgresFixture.OpenAsync(database.PlatformConnectionString);
        await using var command = new NpgsqlCommand("""
            SELECT operation, outcome::text, tenant_id, metadata::text, coalesce(before_state::text, ''), coalesce(after_state::text, ''),
                coalesce(entity_id, '')
            FROM audit_log WHERE correlation_id = @c ORDER BY timestamp, id
            """, (NpgsqlConnection)connection);
        command.Parameters.AddWithValue("c", correlation);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<AuditRow>();
        while (await reader.ReadAsync())
            rows.Add(new AuditRow(reader.GetString(0), reader.GetString(1), reader.GetGuid(2), reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6)));
        return rows;
    }

    private static IServiceScopeFactory AuditStore() =>
        new ServiceCollection()
            .AddMetrics()
            .AddLogging()
            .AddScoped<IAuditStateCapture, AuditStateCapture>()
            .AddSingleton<IAuditHealth, AuditHealth>()
            .AddSingleton<IAuditCatalog>(new AuditCatalog([new TenancyAuditCatalogSource()]))
            .AddSingleton<ITenantContextAccessor>(StaticTenantContextAccessor.Unresolved)
            .AddScoped<LearnStack.SharedKernel.Identity.IGlobalIdentityEnrollment, GlobalIdentityEnrollment>()
            .AddSingleton(new Lazy<NpgsqlDataSource>(
                () => throw new InvalidOperationException("The scope's rows are written on its own connection.")))
            .AddScoped<IAuditStore, PostgresAuditStore>()
            .BuildServiceProvider()
            .GetRequiredService<IServiceScopeFactory>();

    /// <summary>The real scope, recording the order in which its handle is entered and queried.</summary>
    private sealed class RecordingScope(IPlatformAdminScope inner, List<string> events) : IPlatformAdminScope
    {
        public async Task<IPlatformAdminScopeHandle> EnterAsync(
            PlatformAdmission admission, CancellationToken cancellationToken = default,
            [System.Runtime.CompilerServices.CallerMemberName] string? callerMember = null,
            [System.Runtime.CompilerServices.CallerFilePath] string? callerFile = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0)
        {
            events.Add("enter");
            return new Handle(await inner.EnterAsync(admission, cancellationToken, callerMember, callerFile, callerLine), events);
        }

        private sealed class Handle(IPlatformAdminScopeHandle inner, List<string> events) : IPlatformAdminScopeHandle
        {
            public Task<int> ExecuteAsync(string sql, IReadOnlyList<PlatformSqlParameter>? parameters = null, CancellationToken cancellationToken = default) =>
                inner.ExecuteAsync(sql, parameters, cancellationToken);

            public Task<IReadOnlyList<T>> QueryAsync<T>(
                string sql, IReadOnlyList<PlatformSqlParameter>? parameters, Func<System.Data.Common.DbDataReader, T> map,
                CancellationToken cancellationToken = default)
            {
                events.Add("query");
                return inner.QueryAsync(sql, parameters, map, cancellationToken);
            }

            public Task WriteIntentAsync(PlatformResult intent, CancellationToken cancellationToken = default) =>
                inner.WriteIntentAsync(intent, cancellationToken);

            public Task<AuditRedactionReport> RedactAuditAsync(AuditRedactionPlan plan, CancellationToken cancellationToken = default) =>
                inner.RedactAuditAsync(plan, cancellationToken);

            public Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default) =>
                inner.WriteResultAsync(result, cancellationToken);

            public Task CommitAsync(CancellationToken cancellationToken = default) => inner.CommitAsync(cancellationToken);

            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }

    /// <summary>What a faulting handle does, each at the moment the owner reaches that call.</summary>
    private sealed class HandleFaults
    {
        public Exception? Commit { get; init; }
        public Exception? Dispose { get; init; }
        public bool HangResult { get; init; }
    }

    /// <summary>The real scope with a handle that fails its commit or its disposal, or hangs its result write until cut.</summary>
    private sealed class FaultingScope(IPlatformAdminScope inner, HandleFaults faults) : IPlatformAdminScope
    {
        public async Task<IPlatformAdminScopeHandle> EnterAsync(
            PlatformAdmission admission, CancellationToken cancellationToken = default,
            [System.Runtime.CompilerServices.CallerMemberName] string? callerMember = null,
            [System.Runtime.CompilerServices.CallerFilePath] string? callerFile = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0) =>
            new Handle(await inner.EnterAsync(admission, cancellationToken, callerMember, callerFile, callerLine), faults);

        private sealed class Handle(IPlatformAdminScopeHandle real, HandleFaults faults) : IPlatformAdminScopeHandle
        {
            public Task<int> ExecuteAsync(string sql, IReadOnlyList<PlatformSqlParameter>? parameters = null, CancellationToken cancellationToken = default) =>
                real.ExecuteAsync(sql, parameters, cancellationToken);

            public Task<IReadOnlyList<T>> QueryAsync<T>(
                string sql, IReadOnlyList<PlatformSqlParameter>? parameters, Func<System.Data.Common.DbDataReader, T> map,
                CancellationToken cancellationToken = default) => real.QueryAsync(sql, parameters, map, cancellationToken);

            public Task WriteIntentAsync(PlatformResult intent, CancellationToken cancellationToken = default) =>
                real.WriteIntentAsync(intent, cancellationToken);

            public Task<AuditRedactionReport> RedactAuditAsync(AuditRedactionPlan plan, CancellationToken cancellationToken = default) =>
                real.RedactAuditAsync(plan, cancellationToken);

            public async Task WriteResultAsync(PlatformResult result, CancellationToken cancellationToken = default)
            {
                if (faults.HangResult) await Task.Delay(Timeout.Infinite, cancellationToken);
                await real.WriteResultAsync(result, cancellationToken);
            }

            public Task CommitAsync(CancellationToken cancellationToken = default) =>
                faults.Commit is { } fault ? Task.FromException(fault) : real.CommitAsync(cancellationToken);

            public async ValueTask DisposeAsync()
            {
                await real.DisposeAsync();
                if (faults.Dispose is { } fault) throw fault;
            }
        }
    }

    /// <summary>A scope whose database cannot be reached: entering it fails with the fault it was given.</summary>
    private sealed class ThrowingScope(Exception fault) : IPlatformAdminScope
    {
        public Task<IPlatformAdminScopeHandle> EnterAsync(
            PlatformAdmission admission, CancellationToken cancellationToken = default,
            [System.Runtime.CompilerServices.CallerMemberName] string? callerMember = null,
            [System.Runtime.CompilerServices.CallerFilePath] string? callerFile = null,
            [System.Runtime.CompilerServices.CallerLineNumber] int callerLine = 0) => throw fault;
    }

    private sealed record AuditRow(string Operation, string Outcome, Guid Tenant, string Metadata, string Before, string After, string EntityId);

    /// <summary>Fails the intent or the completion row of the operator, as an outage at that moment would.</summary>
    private sealed class FailingStore(
        IServiceScopeFactory inner, bool failIntent, bool failResult, Func<System.Data.Common.DbConnection, Task>? afterIntent = null)
        : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope(inner.CreateScope(), failIntent, failResult, afterIntent);

        private sealed class Scope(
            IServiceScope inner, bool failIntent, bool failResult, Func<System.Data.Common.DbConnection, Task>? afterIntent)
            : IServiceScope, IServiceProvider
        {
            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type type) => type == typeof(IAuditStore)
                ? new Decorator(inner.ServiceProvider.GetRequiredService<IAuditStore>(), failIntent, failResult, afterIntent)
                : inner.ServiceProvider.GetService(type);

            public void Dispose() => inner.Dispose();
        }

        private sealed class Decorator(
            IAuditStore store, bool failIntent, bool failResult, Func<System.Data.Common.DbConnection, Task>? afterIntent) : IAuditStore
        {
            public Task WritePlatformScopeAsync(AuditEntryDraft entry, System.Data.Common.DbConnection connection,
                System.Data.Common.DbTransaction transaction, CancellationToken cancellationToken = default) =>
                store.WritePlatformScopeAsync(entry, connection, transaction, cancellationToken);

            public async Task WritePlatformOperatorIntentAsync(AuditEntryDraft entry, PlatformAdmission admission,
                System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
                CancellationToken cancellationToken = default)
            {
                if (failIntent) throw new AuditWriteFailedException("forced intent failure");
                await store.WritePlatformOperatorIntentAsync(entry, admission, connection, transaction, cancellationToken);
                // Between the intent and the provider call, as a tenant that commits a row at that moment would.
                if (afterIntent is not null) await afterIntent(connection);
            }

            public Task WritePlatformOperatorResultAsync(AuditEntryDraft entry, PlatformAdmission admission,
                System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
                CancellationToken cancellationToken = default) =>
                failResult
                    ? throw new AuditWriteFailedException("forced result failure")
                    : store.WritePlatformOperatorResultAsync(entry, admission, connection, transaction, cancellationToken);

            public Task WriteGlobalIdentityPendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unit,
                GlobalIdentityCreationReference creation, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task ReconcileGlobalIdentityAsync(AuditIntent intent, IReadOnlyList<CapturedEntityChange> changes,
                AuditIntentState state, DateTimeOffset timestamp, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task WritePendingAsync(LearnStack.SharedKernel.Persistence.IUnitOfWork unitOfWork,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task WriteStandaloneAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task WriteBestEffortAsync(AuditEntryDraft entry, CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task WriteJobTerminalAsync(AuditEntryDraft entry, JobTerminalTransition transition,
                System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();

            public Task WriteEventTerminalAsync(AuditEntryDraft entry, EventTerminalTransition transition,
                System.Data.Common.DbConnection connection, System.Data.Common.DbTransaction transaction,
                CancellationToken cancellationToken = default) => throw new NotSupportedException();
        }
    }

    /// <summary>A scripted realm: what the provider lists, what it says about one alias, and how a change ends.</summary>
    private sealed class Scripted : IFederationOperator
    {
        public IReadOnlyList<FederationBrokerFacts> Listed { get; set; } = [];
        public bool Unavailable { get; set; }
        public List<string>? Events { get; set; }
        public FederationBrokerFacts? Inspected { get; set; }
        public int InspectedMappers { get; set; }
        public FederationInspectionStatus? InspectedStatus { get; set; }
        public FederationOperatorOutcome RemoveOutcome { get; set; } = FederationOperatorOutcome.Applied;
        public FederationOperatorOutcome DisableOutcome { get; set; } = FederationOperatorOutcome.Applied;
        public Func<Task>? BeforeRemove { get; set; }
        public Func<Task>? BeforeDisable { get; set; }
        public Exception? ListFailure { get; set; }
        public List<bool> RemoveRequirements { get; } = [];
        public Dictionary<string, int?> MapperCounts { get; } = new(StringComparer.Ordinal);
        public HashSet<string> MapperDeadlines { get; } = new(StringComparer.Ordinal);
        public List<string> Calls { get; } = [];
        public List<int> Offsets { get; } = [];
        public List<string> MapperCalls { get; } = [];
        public List<FederationBrokerOwner> Removed { get; } = [];
        public List<FederationBrokerOwner> Disabled { get; } = [];

        public Task<FederationBrokerPage> ListAsync(int offset, int limit, CancellationToken cancellationToken = default)
        {
            Calls.Add("list");
            Events?.Add("list");
            Offsets.Add(offset);
            if (ListFailure is not null) return Task.FromException<FederationBrokerPage>(ListFailure);
            if (Unavailable) return Task.FromResult(FederationBrokerPage.Unavailable);
            var page = Listed.Skip(offset).Take(limit).ToList();
            return Task.FromResult(new FederationBrokerPage(FederationOperatorStatus.Ok, page, 0, page.Count));
        }

        public Task<FederationBrokerInspection> InspectAsync(string brokerAlias, CancellationToken cancellationToken = default)
        {
            Calls.Add("inspect");
            return Task.FromResult(InspectedStatus switch
            {
                FederationInspectionStatus.Absent => FederationBrokerInspection.Absent,
                FederationInspectionStatus.NotOwned => FederationBrokerInspection.NotOwned,
                FederationInspectionStatus.Unavailable => FederationBrokerInspection.Unavailable,
                _ => new FederationBrokerInspection(FederationInspectionStatus.Owned, Inspected, InspectedMappers),
            });
        }

        public Task<int?> CountMappersAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
        {
            Calls.Add("mappers");
            Events?.Add("mappers");
            MapperCalls.Add(owner.Alias);
            if (MapperDeadlines.Contains(owner.Alias)) throw new OperationCanceledException("the page's time ran out");
            return Task.FromResult(MapperCounts.TryGetValue(owner.Alias, out var count) ? count : 0);
        }

        public async Task<FederationOperatorOutcome> RemoveAsync(
            FederationBrokerOwner owner, bool requireDisabledUnreadable = false, CancellationToken cancellationToken = default)
        {
            Calls.Add("remove");
            Removed.Add(owner);
            RemoveRequirements.Add(requireDisabledUnreadable);
            if (BeforeRemove is { } hook) await hook();
            return RemoveOutcome;
        }

        public async Task<FederationOperatorOutcome> DisableAsync(FederationBrokerOwner owner, CancellationToken cancellationToken = default)
        {
            Calls.Add("disable");
            Disabled.Add(owner);
            if (BeforeDisable is { } hook) await hook();
            return DisableOutcome;
        }
    }
}
