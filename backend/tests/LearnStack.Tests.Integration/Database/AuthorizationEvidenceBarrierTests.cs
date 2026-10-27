using System.Diagnostics;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.Tests.AuthorizationEvidenceProof;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Fixture-policy mechanism proof through the actual common owners and application-role module/store paths.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class AuthorizationEvidenceBarrierTests(SchemaFixture schema)
{
    [Theory]
    [InlineData("read-write")]
    [InlineData("read-only")]
    [InlineData("ownerless")]
    public async Task An_absorbed_admitted_inner_refusal_survives_its_independent_token_cancellation_before_observation(string owner)
    {
        var correlation = Correlation();
        var writable = owner == "read-write";
        await using var proof = await ProofAsync(correlation, writable ? ResourcePopulation.Plural : ResourcePopulation.Off);
        proof.InnerOutcome = ProtectedOperationOutcome.Allowed;
        proof.CancelInnerBeforeObservation = true;
        proof.Factory.HoldCommit = !writable;
        var running = proof.RunAsync(writable ? TransactionMode.ReadWrite : owner == "read-only" ? TransactionMode.ReadOnly : null);
        try
        {
            if (!writable)
            {
                try
                {
                    await proof.Factory.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
                    running.IsCompleted.Should().BeFalse("an absorbed canceled act still requires durable standalone evidence before disclosure");
                    (await ObserveAsync(correlation, proof.BusinessId)).Rows.Should().BeEmpty();
                }
                finally { proof.Factory.CommitRelease.TrySetResult(); }
            }
            (await running).IsSuccess.Should().BeTrue();
            proof.InnerResult!.Error!.Code.Should().Be("forbidden");
            proof.DeniedHandlerEntered.Should().BeTrue("this handler was admitted and itself decided the refusal");
            proof.InnerCancellationRequested.Should().BeTrue();
            proof.InnerAuthorityRevoked.Should().BeTrue();
            proof.OuterAuthorityStillLive.Should().BeTrue("the independent inner token cannot revoke the outer operation");
            var observed = await ObserveAsync(correlation, proof.BusinessId);
            observed.Business.Should().Be(writable);
            observed.Rows.Should().HaveCount(writable ? 3 : 1);
            var row = observed.Rows.Should().ContainSingle(item => item.Operation == "identity.authorization.deny").Subject;
            row.Outcome.Should().Be("denied");
            row.Policy.Should().Be("proof.evidence.inner");
            var occurrence = writable ? proof.Store.Pending.Should().ContainSingle().Subject : proof.Store.Standalone.Should().ContainSingle().Subject;
            row.Id.Should().Be(occurrence.Draft.Id.Value);
            row.Timestamp.Should().Be(occurrence.Draft.Timestamp);
            if (writable)
            {
                proof.Store.Standalone.Should().BeEmpty();
                proof.Factory.Opens.Should().Be(0);
            }
            else proof.Store.Pending.Should().BeEmpty();
            proof.Unit.HasActiveTransaction.Should().BeFalse();
        }
        finally
        {
            proof.Factory.CommitRelease.TrySetResult();
            await ((Task)running).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
            await CleanupAsync(correlation, proof.BusinessId);
        }
    }

    [Theory]
    [InlineData(ResourcePopulation.Off, 0)]
    [InlineData(ResourcePopulation.Should, 1)]
    [InlineData(ResourcePopulation.May, 1)]
    [InlineData(ResourcePopulation.OverrideOff, 0)]
    [InlineData(ResourcePopulation.Plural, 2)]
    public async Task Authorization_Denials_Have_PreSuccess_Durability_And_Stable_Identity(ResourcePopulation population, int resources)
    {
        var correlation = Correlation();
        await using var proof = await ProofAsync(correlation, population);
        try
        {
            var response = await proof.RunAsync();
            response.IsSuccess.Should().BeTrue();
            proof.InnerResult!.IsFailure.Should().BeTrue();
            proof.DeniedHandlerEntered.Should().BeFalse();
            var observed = await ObserveAsync(correlation, proof.BusinessId);
            observed.Business.Should().BeTrue();
            observed.Rows.Should().HaveCount(resources + 1);
            var denial = observed.Rows.Should().ContainSingle(row => row.Operation == "identity.authorization.deny").Subject;
            denial.Outcome.Should().Be("denied");
            denial.Policy.Should().Be("proof.evidence.inner");
            var pending = proof.Store.Pending.Should().ContainSingle().Subject;
            denial.Id.Should().Be(pending.Draft.Id.Value);
            denial.Timestamp.Should().Be(pending.Draft.Timestamp);
            proof.Store.Standalone.Should().BeEmpty("the event was acknowledged by the actual business commit");
            proof.Factory.Opens.Should().Be(0);
            proof.Unit.HasActiveTransaction.Should().BeFalse();
            if (population == ResourcePopulation.Plural)
                observed.Rows.Where(row => row.Operation.StartsWith("proof.", StringComparison.Ordinal))
                    .Should().OnlyContain(row => row.Outcome == "success" && row.Policy == "proof.evidence.outer");
            if (population is ResourcePopulation.Should or ResourcePopulation.May)
                observed.Rows.Should().ContainSingle(row => row.Operation == "proof.inner.read" && row.Outcome == "denied");
        }
        finally { await CleanupAsync(correlation, proof.BusinessId); }
    }

    [Fact]
    public async Task A_real_denial_INSERT_failure_rolls_back_the_actual_module_write_and_refuses_outer_success()
    {
        var correlation = Correlation();
        await using var proof = await ProofAsync(correlation);
        var trigger = await InstallFailureAsync(correlation);
        try
        {
            Result<None>? published = null;
            var failure = await FluentActions.Awaiting(async () => published = await proof.RunAsync())
                .Should().ThrowAsync<AuditWriteFailedException>();
            HttpStatusMap.For(failure.Which).Should().Be(503);
            published.Should().BeNull();
            var observed = await ObserveAsync(correlation, proof.BusinessId);
            observed.Business.Should().BeFalse();
            observed.Rows.Should().BeEmpty();
            proof.Unit.HasActiveTransaction.Should().BeFalse();
            proof.Store.Pending.Should().ContainSingle();
            proof.Store.Standalone.Should().ContainSingle().Which.Should().BeSameAs(proof.Store.Pending[0]);
            proof.Health.IsHealthy.Should().BeFalse();
            proof.StoreLogs.Entries.Should().ContainSingle(item => item.Level == LogLevel.Critical);
            proof.StoreLogs.Entries.Should().OnlyContain(item => item.Exception == null);
        }
        finally { await RemoveFailureAsync(trigger); await CleanupAsync(correlation, proof.BusinessId); }
    }

    [Fact]
    public async Task Trusted_composition_failure_prevents_an_absorbing_RW_owner_from_committing_any_business_state()
    {
        var correlation = Correlation();
        await using var proof = await ProofAsync(correlation, compositionFault: true);
        try
        {
            await FluentActions.Awaiting(() => proof.RunAsync()).Should().ThrowAsync<AuditWriteFailedException>();
            var observed = await ObserveAsync(correlation, proof.BusinessId);
            observed.Business.Should().BeFalse();
            observed.Rows.Should().BeEmpty();
            proof.CompositionLogs.Entries.Should().ContainSingle(item => item.Level == LogLevel.Critical && item.Exception == null);
            proof.Store.Pending.Should().BeEmpty();
            proof.Store.Standalone.Should().BeEmpty();
        }
        finally { await CleanupAsync(correlation, proof.BusinessId); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Physical_RO_and_ownerless_success_wait_for_standalone_COMMIT_acknowledgement_before_disclosure(bool readOnly)
    {
        var correlation = Correlation();
        await using var proof = await ProofAsync(correlation);
        proof.Factory.HoldCommit = true;
        var running = proof.RunAsync(readOnly ? TransactionMode.ReadOnly : null);
        try
        {
            await proof.Factory.CommitEntered.Task.WaitAsync(TimeSpan.FromSeconds(4));
            running.IsCompleted.Should().BeFalse();
            proof.Unit.Mode.Should().Be(readOnly ? TransactionMode.ReadOnly : null);
            proof.Store.Pending.Should().BeEmpty("physical ReadOnly cannot insert or upgrade its mode");
            (await ObserveAsync(correlation, proof.BusinessId)).Rows.Should().BeEmpty("the independent evidence transaction has not committed");
        }
        finally { proof.Factory.CommitRelease.TrySetResult(); }
        try
        {
            (await running).IsSuccess.Should().BeTrue();
            proof.Store.Standalone.Should().ContainSingle();
            var observed = await ObserveAsync(correlation, proof.BusinessId);
            observed.Business.Should().BeFalse();
            observed.Rows.Should().ContainSingle(row => row.Operation == "identity.authorization.deny");
            proof.Unit.HasActiveTransaction.Should().BeFalse();
        }
        finally { await CleanupAsync(correlation, proof.BusinessId); }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_lost_evidence_COMMIT_acknowledgement_refuses_otherwise_successful_RO_or_ownerless_disclosure(bool readOnly)
    {
        var correlation = Correlation();
        await using var proof = await ProofAsync(correlation);
        proof.Factory.FailAcknowledgement = true;
        try
        {
            Result<None>? published = null;
            var failure = await FluentActions.Awaiting(async () => published = await proof.RunAsync(readOnly ? TransactionMode.ReadOnly : null))
                .Should().ThrowAsync<AuditWriteFailedException>();
            HttpStatusMap.For(failure.Which).Should().Be(503);
            published.Should().BeNull();
            proof.Factory.Opens.Should().Be(1, "finally cannot repeat a failed standalone attempt");
            proof.Store.Standalone.Should().ContainSingle();
            proof.Health.IsHealthy.Should().BeFalse();
            proof.Unit.HasActiveTransaction.Should().BeFalse();
            (await ObserveAsync(correlation, proof.BusinessId)).Rows.Should().ContainSingle(
                "a committed row without an acknowledged write still cannot authorize disclosure");
        }
        finally { await CleanupAsync(correlation, proof.BusinessId); }
    }

    [Theory]
    [InlineData(ProtectedOperationOutcome.Forbidden, 403)]
    [InlineData(ProtectedOperationOutcome.NotCovered, 404)]
    public async Task Already_refused_403_or_404_survives_evidence_acknowledgement_failure_without_dispatch(ProtectedOperationOutcome outcome, int status)
    {
        var correlation = Correlation();
        await using var proof = await ProofAsync(correlation);
        proof.InnerOutcome = outcome;
        proof.Factory.FailAcknowledgement = true;
        try
        {
            var result = await proof.RefuseAsync();
            result.IsFailure.Should().BeTrue();
            HttpStatusMap.For(result.Error!).Should().Be(status);
            proof.DeniedHandlerEntered.Should().BeFalse();
            proof.Factory.Opens.Should().Be(1);
            proof.StoreLogs.Entries.Should().ContainSingle(item => item.Level == LogLevel.Critical);
            proof.CompositionLogs.Entries.Should().BeEmpty();
        }
        finally { await CleanupAsync(correlation, proof.BusinessId); }
    }

    [Fact]
    public async Task Exact_event_reconciliation_does_not_promote_an_unknown_business_COMMIT_to_success()
    {
        var correlation = Correlation();
        await using var proof = await ProofAsync(correlation, commitBoundary: committed => committed
            ? throw new IOException("Deliberate business COMMIT acknowledgement loss.") : Task.CompletedTask);
        try
        {
            Result<None>? published = null;
            await FluentActions.Awaiting(async () => published = await proof.RunAsync()).Should().ThrowAsync<IOException>();
            published.Should().BeNull();
            proof.Store.ReconciliationStates.Should().Equal(AuditIntentState.Indeterminate);
            var pending = proof.Store.Pending.Should().ContainSingle().Subject;
            proof.Store.Standalone.Should().ContainSingle().Which.Should().BeSameAs(pending);
            var observed = await ObserveAsync(correlation, proof.BusinessId);
            observed.Business.Should().BeTrue("the deliberately lost acknowledgement followed the physical commit");
            var row = observed.Rows.Should().ContainSingle().Subject;
            row.Id.Should().Be(pending.Draft.Id.Value);
            row.Timestamp.Should().Be(pending.Draft.Timestamp);
            row.Outcome.Should().Be("denied");
            proof.StoreLogs.Entries.Should().ContainSingle(item => item.Level == LogLevel.Warning
                && item.Text.Contains("no business transaction outcome", StringComparison.Ordinal));
        }
        finally { await CleanupAsync(correlation, proof.BusinessId); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SIGKILL_before_or_immediately_after_physical_COMMIT_proves_atomic_denial_durability_without_finally(bool afterCommit)
    {
        var correlation = Correlation();
        var business = TenantSettingId.From(Guid.CreateVersion7());
        var childDll = Path.Combine(AppContext.BaseDirectory, "authorization-evidence-probe", "LearnStack.Tests.AuthorizationEvidenceProbe.dll");
        File.Exists(childDll).Should().BeTrue("the separately built evidence probe must be staged by the Integration project");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(childDll);
        start.ArgumentList.Add(afterCommit ? "postcommit" : "precommit");
        start.Environment.Clear();
        foreach (var key in new[] { "PATH", "DOTNET_ROOT", "DOTNET_ROOT_ARM64", "TMPDIR", "LANG" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        start.Environment["DOTNET_EnableDiagnostics"] = "0";
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        start.Environment["COMPlus_DbgEnableMiniDump"] = "0";
        start.Environment["LEARNSTACK_EVIDENCE_PROOF_APP"] = schema.Postgres.AppConnectionString;
        start.Environment["LEARNSTACK_EVIDENCE_PROOF_AUTHORITY"] = IdentityMembershipFixture.Authority.AuthorityId.Value.ToString("D");
        start.Environment["LEARNSTACK_EVIDENCE_PROOF_ISSUER"] = IdentityMembershipFixture.Authority.Issuer;
        start.Environment["LEARNSTACK_EVIDENCE_PROOF_TENANT"] = SchemaFixture.TenantA.ToString("D");
        start.Environment["LEARNSTACK_EVIDENCE_PROOF_ORGANIZATION"] = SchemaFixture.OrgA1.ToString("D");
        start.Environment["LEARNSTACK_EVIDENCE_PROOF_ACTOR"] = IdentityMembershipFixture.AdminA.ToString("D");
        start.Environment["LEARNSTACK_EVIDENCE_PROOF_BUSINESS"] = business.Value.ToString("D");
        start.Environment["LEARNSTACK_EVIDENCE_PROOF_CORRELATION"] = correlation;
        using var child = new Process { StartInfo = start };
        child.Start().Should().BeTrue();
        var errors = DrainBoundedAsync(child.StandardError);
        try
        {
            var marker = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            marker.Should().Be("LearnStackEvidenceBoundary:" + (afterCommit ? "postcommit" : "precommit"));
            child.HasExited.Should().BeFalse();
            var beforeKill = await ObserveAsync(correlation, business);
            beforeKill.Business.Should().Be(afterCommit);
            beforeKill.Rows.Should().HaveCount(afterCommit ? 1 : 0);
            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            var tail = await DrainBoundedAsync(child.StandardOutput);
            tail.Should().NotContain("finally");
            (await errors).Should().BeEmpty();
            child.ExitCode.Should().NotBe(0);
            var afterKill = await ObserveAsync(correlation, business);
            afterKill.Business.Should().Be(afterCommit);
            afterKill.Rows.Should().HaveCount(afterCommit ? 1 : 0);
            if (afterCommit) afterKill.Rows[0].Outcome.Should().Be("denied");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            await errors;
            await CleanupAsync(correlation, business);
        }
    }

    private Task<AuthorizationBarrierHarness> ProofAsync(string correlation, ResourcePopulation population = ResourcePopulation.Off,
        bool compositionFault = false, Func<bool, Task>? commitBoundary = null) => AuthorizationBarrierHarness.CreateAsync(
        schema.Postgres.AppConnectionString, IdentityMembershipFixture.Authority.AuthorityId, IdentityMembershipFixture.Authority.Issuer,
        TenantId.From(SchemaFixture.TenantA), OrganizationId.From(SchemaFixture.OrgA1), UserId.From(IdentityMembershipFixture.AdminA),
        correlation, population, compositionFault, commitBoundary);

    private async Task<(bool Business, List<Row> Rows)> ObserveAsync(string correlation, TenantSettingId business)
    {
        await using var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString);
        await SchemaQueries.AssertApplicationRoleAsync(app);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction);
        await using var setting = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM tenant_settings WHERE tenant_id=@tenant AND id=@id)", app, transaction);
        setting.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        setting.Parameters.AddWithValue("id", business.Value);
        var exists = (bool)(await setting.ExecuteScalarAsync())!;
        await using var audit = new NpgsqlCommand("""
            SELECT id,timestamp,operation,outcome,metadata->'authorization'->>'policy'
            FROM audit_log WHERE tenant_id=@tenant AND correlation_id=@correlation ORDER BY operation
            """, app, transaction);
        audit.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
        audit.Parameters.AddWithValue("correlation", correlation);
        await using var reader = await audit.ExecuteReaderAsync();
        var rows = new List<Row>();
        while (await reader.ReadAsync()) rows.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetString(2), reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4)));
        return (exists, rows);
    }

    private async Task CleanupAsync(string correlation, TenantSettingId business)
    {
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.AppConnectionString))
        {
            await using var transaction = await app.BeginTransactionAsync();
            await ScopeAsync(app, transaction);
            await using var command = new NpgsqlCommand("DELETE FROM tenant_settings WHERE tenant_id=@tenant AND id=@id", app, transaction);
            command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA);
            command.Parameters.AddWithValue("id", business.Value);
            await command.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        await using var platform = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.PlatformConnectionString);
        await using var audit = new NpgsqlCommand("DELETE FROM audit_log WHERE correlation_id=@correlation", platform);
        audit.Parameters.AddWithValue("correlation", correlation);
        await audit.ExecuteNonQueryAsync();
    }

    private async Task<string> InstallFailureAsync(string correlation)
    {
        var name = "evidence_barrier_fault_" + Guid.NewGuid().ToString("N");
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand($$"""
            CREATE FUNCTION public.{{name}}() RETURNS trigger LANGUAGE plpgsql AS $proof$
            BEGIN
                IF NEW.operation='identity.authorization.deny' AND NEW.correlation_id='{{correlation}}' THEN
                    RAISE EXCEPTION 'Deliberate barrier proof refusal' USING ERRCODE='P0399';
                END IF;
                RETURN NEW;
            END $proof$;
            CREATE TRIGGER {{name}} BEFORE INSERT ON public.audit_log
                FOR EACH ROW EXECUTE FUNCTION public.{{name}}();
            """, owner);
        await command.ExecuteNonQueryAsync();
        return name;
    }

    private async Task RemoveFailureAsync(string name)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.MigrationConnectionString);
        await using var command = new NpgsqlCommand($"DROP TRIGGER {name} ON public.audit_log; DROP FUNCTION public.{name}();", owner);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ScopeAsync(NpgsqlConnection app, NpgsqlTransaction transaction)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @tenant, true), set_config('app.organization_id', @organization, true)", app, transaction);
        command.Parameters.AddWithValue("tenant", SchemaFixture.TenantA.ToString("D"));
        command.Parameters.AddWithValue("organization", SchemaFixture.OrgA1.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> DrainBoundedAsync(StreamReader stream)
    {
        var buffer = new char[512];
        var result = new System.Text.StringBuilder(1024);
        int read;
        while ((read = await stream.ReadAsync(buffer)) != 0)
            if (result.Length < 2048) result.Append(buffer, 0, Math.Min(read, 2048 - result.Length));
        return result.ToString();
    }

    private static string Correlation() => "evidence-barrier-" + Guid.NewGuid().ToString("N");
    private sealed record Row(Guid Id, DateTimeOffset Timestamp, string Operation, string Outcome, string? Policy);
}
