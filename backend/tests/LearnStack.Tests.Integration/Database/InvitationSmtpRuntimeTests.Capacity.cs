using System.Data;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Email;
using LearnStack.Infrastructure.Email.Smtp;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Polly;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class InvitationSmtpRuntimeTests
{
    [Fact]
    public async Task Four_real_provider_owners_refuse_a_fifth_eligible_tenant_before_claim_or_SMTP()
    {
        await using var database = await OpenDatabaseAsync();
        await using var smtp = new InvitationSmtpTransportServer(InvitationSmtpTransportBehavior.Accept);
        var held = new HeldActualSmtp(smtp.Port, expected: 4);
        await using var host = Host(smtp.Port, configure: services =>
        {
            services.RemoveAll<IEmailProvider>();
            services.AddSingleton<IEmailProvider>(held);
        });
        var source = host.Services.GetRequiredService<InvitationSmtpProviderDataSource>();
        var calls = new List<Task<JobExternalExecutionResult>>();
        var originals = new List<ConfirmedStartReceipt>();
        NpgsqlConnection[] retainedConnections = [];
        try
        {
            for (var index = 0; index < 4; index++)
            {
                var facts = await SeedAsync();
                var flow = await PrepareAndArmAsync(host.Services, facts.Job);
                originals.Add(flow.Receipt);
                calls.Add(flow.Executor.ExecuteExternalAsync(flow.Read, flow.Arm, CancellationToken.None));
            }
            await held.AllAccepted.Task.WaitAsync(TimeSpan.FromSeconds(20));
            calls.Should().OnlyContain(call => !call.IsCompleted);
            smtp.Connections.Should().Be(4);
            smtp.Messages.Should().Be(4);
            held.AcceptedWithCleanup.Should().Be(4);
            // SMTP has positively cleaned up. The distinct provider SQL owners
            // and their physical connectors still belong to unfinished delivery.
            var owners = SmtpOwners(source);
            owners.Should().HaveCount(4);
            retainedConnections = owners.Select(owner => SmtpField<NpgsqlConnection>(owner, "_connection")).ToArray();
            retainedConnections.Should().OnlyContain(connection => connection.State == ConnectionState.Open);
            retainedConnections.Select(connection => connection.ProcessID).Should().OnlyHaveUniqueItems();
            foreach (var original in originals)
            {
                var provider = await ReadActualSmtpFactAsync(original.Envelope.JobId.Value);
                provider.Should().NotBeNull();
                provider!.State.Should().Be("sending");
                provider.Receipt.Should().BeNull();
            }

            // Tenant A already owns its four capacity reservations. Tenant B's
            // real admission succeeds, so tenant capacity cannot mask the source ceiling.
            var fifth = await SeedAsync(tenant: SchemaFixture.TenantB);
            var fifthFlow = await PrepareAndArmAsync(host.Services, fifth.Job);
            originals.Add(fifthFlow.Receipt);
            fifthFlow.Receipt.NormalAttemptOrdinal.Should().Be(1);
            var refused = await fifthFlow.Executor.ExecuteExternalAsync(fifthFlow.Read, fifthFlow.Arm, CancellationToken.None);
            refused.Status.Should().Be(JobExternalExecutionStatus.Reported);
            refused.ProviderIssued.Should().BeTrue();
            refused.CleanupFailed.Should().BeFalse();
            refused.Observation!.State.Should().Be(BackgroundJobExternalState.Unknown);
            (await ReadActualSmtpFactAsync(fifth.Job.Value)).Should().BeNull("source refusal precedes the provider claim");
            SmtpOwners(source).Should().Equal(owners);
            source.IsHealthy.Should().BeTrue("saturation is not failed cleanup");
            smtp.Connections.Should().Be(4);
            smtp.Messages.Should().Be(4);
            held.AcceptedWithCleanup.Should().Be(4);
        }
        finally
        {
            held.Release.TrySetResult();
            try { await Task.WhenAll(calls); }
            finally
            {
                foreach (var original in originals)
                {
                    original.Run.Close();
                    original.SourceOwner.CloseIssuance();
                    original.SourceOwner.CleanupConfirmed.Should().BeTrue();
                }
            }
        }
        calls.Select(call => call.Result.Observation!.State).Should().OnlyContain(state => state == BackgroundJobExternalState.Succeeded);
        calls.Should().OnlyContain(call => !call.Result.CleanupFailed);
        SmtpOwners(source).Should().BeEmpty();
        retainedConnections.Should().OnlyContain(connection => connection.State == ConnectionState.Closed);
        source.IsHealthy.Should().BeTrue();
        var resumed = await SeedAsync(tenant: SchemaFixture.TenantB);
        var resumedFlow = await PrepareAndArmAsync(host.Services, resumed.Job);
        try
        {
            var resumedResult = await resumedFlow.Executor.ExecuteExternalAsync(resumedFlow.Read, resumedFlow.Arm, CancellationToken.None);
            resumedResult.Status.Should().Be(JobExternalExecutionStatus.Reported);
            resumedResult.Observation!.State.Should().Be(BackgroundJobExternalState.Succeeded);
            resumedResult.CleanupFailed.Should().BeFalse();
            smtp.Connections.Should().Be(5);
            smtp.Messages.Should().Be(5);
            SmtpOwners(source).Should().BeEmpty();
        }
        finally
        {
            resumedFlow.Receipt.Run.Close();
            resumedFlow.Receipt.SourceOwner.CloseIssuance();
            resumedFlow.Receipt.SourceOwner.CleanupConfirmed.Should().BeTrue();
        }
    }

    private sealed class HeldActualSmtp(int port, int expected) : IEmailProvider
    {
        private readonly SmtpEmailProvider _actual = new(
            new("127.0.0.1", port, "sender@example.test", SmtpEmailTlsMode.DevelopmentPlaintext,
                null, null, AllowDevelopmentPlaintext: true),
            new InvitationSmtpTransportResilience(ResiliencePipeline.Empty));
        private int _accepted;
        internal int AcceptedWithCleanup => Volatile.Read(ref _accepted);
        internal TaskCompletionSource AllAccepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<EmailSubmissionObservation> SubmitAsync(EmailSubmission message, CancellationToken cancellationToken = default)
        {
            var actual = await _actual.SubmitAsync(message, cancellationToken);
            if (actual != new EmailSubmissionObservation(EmailSubmissionState.Accepted, true))
                throw new InvalidOperationException("Owned SMTP acceptance and cleanup were not observed.");
            if (Interlocked.Increment(ref _accepted) == expected) AllAccepted.TrySetResult();
            // This holds the real observation, never invents acceptance or a receipt.
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
            return actual;
        }
    }

    private static T SmtpField<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static object[] SmtpOwners(InvitationSmtpProviderDataSource source)
    {
        lock (SmtpField<object>(source, "_gate"))
            return SmtpField<System.Collections.IEnumerable>(source, "_owners").Cast<object>().ToArray();
    }

    // Owner-only provider-table observations are fixture instrumentation, not
    // application isolation proof. No ciphertext or message field is selected.
    private async Task<ActualSmtpFact?> ReadActualSmtpFactAsync(Guid job)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database!.MigrationConnectionString);
        await using var query = new NpgsqlCommand("""
            SELECT r.tenant_id,r.organization_id,r.envelope_id,r.job_id,r.attempt_id,r.operation_id,
                r.plan_fingerprint,r.claim_id,r.admission_token,r.original_deadline,r.state,r.receipt_id,r.evidence_id,
                e.invitation_id,e.generation_id
            FROM invitation_email_attempt_receipts r JOIN invitation_email_envelopes e
                ON e.tenant_id=r.tenant_id AND e.id=r.envelope_id WHERE r.job_id=@job
            """, owner) { CommandTimeout = 5 };
        query.Parameters.AddWithValue("job", job);
        await using var row = await query.ExecuteReaderAsync();
        if (!await row.ReadAsync()) return null;
        var fact = new ActualSmtpFact(row.GetGuid(0), row.IsDBNull(1) ? null : row.GetGuid(1), row.GetGuid(2),
            row.GetGuid(3), row.GetGuid(4), row.GetGuid(5), row.GetString(6), row.GetGuid(7), row.GetGuid(8),
            row.GetFieldValue<DateTimeOffset>(9), row.GetString(10), row.IsDBNull(11) ? null : row.GetGuid(11),
            row.IsDBNull(12) ? null : row.GetGuid(12), row.GetGuid(13), row.GetGuid(14));
        (await row.ReadAsync()).Should().BeFalse();
        return fact;
    }

    private sealed record ActualSmtpFact(Guid Tenant, Guid? Organization, Guid Envelope, Guid Job, Guid Attempt,
        Guid Operation, string Fingerprint, Guid Claim, Guid Admission, DateTimeOffset Deadline, string State,
        Guid? Receipt, Guid? Evidence, Guid Invitation, Guid Generation);
}
