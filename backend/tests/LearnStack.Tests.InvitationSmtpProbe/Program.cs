using System.Collections;
using System.Data;
using System.Globalization;
using System.Reflection;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Email;
using LearnStack.Infrastructure.Email.Smtp;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Resilience;
using LearnStack.Tests.Integration;
using MailKit.Net.Smtp;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MimeKit;
using Npgsql;
using Polly;

namespace LearnStack.Tests.InvitationSmtpProbe;

internal static class ProbeProgram
{
    private static readonly TextWriter Protocol = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("retained-cleanup" or "after-final250")) return 78;
        // The child exports only its finite protocol, never host/provider diagnostics.
        Console.SetOut(TextWriter.Null);
        Console.SetError(TextWriter.Null);
        var stage = "construction";
        try
        {
            var retained = args[0] == "retained-cleanup";
            var port = int.Parse(Required("PORT"), CultureInfo.InvariantCulture);
            var client = retained ? new DisposalFailureClient() : null;
            MimeMessage? sent = null;
            if (client is not null) client.MessageSent += (_, value) => sent = value.Message;
            var actual = new SmtpEmailProvider(new("127.0.0.1", port, "sender@example.test",
                SmtpEmailTlsMode.DevelopmentPlaintext, null, null, AllowDevelopmentPlaintext: true),
                new ProbeResilience(), () => client ?? new SmtpClient());
            IEmailProvider email = retained ? actual : new AfterFinal250Provider(actual);
            // Both modes deliberately retain work only inside this killed and joined child.
            var host = new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment(Environments.Development);
                builder.UseSetting("ConnectionStrings:Default", Required("APP"));
                builder.UseSetting("ConnectionStrings:OutboxDispatcher", Required("DISPATCHER"));
                builder.UseSetting("InvitationEmail:KeyRing", Required("KEYRING"));
                builder.UseSetting("InvitationEmail:Smtp:Host", "127.0.0.1");
                builder.UseSetting("InvitationEmail:Smtp:Port", port.ToString(CultureInfo.InvariantCulture));
                builder.UseSetting("InvitationEmail:Smtp:Sender", "sender@example.test");
                builder.UseSetting("InvitationEmail:Smtp:Tls", "DevelopmentPlaintext");
                builder.UseSetting("InvitationEmail:Smtp:Username", "");
                builder.UseSetting("InvitationEmail:Smtp:Password", "");
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IEmailProvider>();
                    services.AddSingleton(email);
                    services.RemoveAll<BackgroundJobRuntimeOptions>();
                    services.AddSingleton(new BackgroundJobRuntimeOptions
                    {
                        LeaseDuration = TimeSpan.FromSeconds(20),
                        RenewalInterval = TimeSpan.FromSeconds(1),
                        SqlTimeout = TimeSpan.FromSeconds(3),
                        ProbeTimeout = TimeSpan.FromSeconds(3),
                        ProgressTimeout = TimeSpan.FromSeconds(35),
                        HandoffRepairGrace = TimeSpan.FromSeconds(15),
                    });
                });
            });
            stage = "admission";
            var executor = host.Services.GetRequiredService<JobExecutor>();
            var result = await ExecuteAsync(executor, Guid.Parse(Required("JOB")));
            Require(retained && result.Status == JobExternalExecutionStatus.Reported
                && result.Observation?.State == BackgroundJobExternalState.Unknown, "observation");
            stage = "retention";
            Require(client!.IsConnected && client.DisposalAttempts == 1 && sent is not null, "sdk");
            var transport = (IList)Field(actual, "_retainedCleanup");
            Require(transport.Count == 1, "transport_count");
            var tuple = ((SmtpClient? Client, MimeMessage? Message))transport[0]!;
            Require(ReferenceEquals(tuple.Client, client) && ReferenceEquals(tuple.Message, sent), "transport_identity");
            var source = host.Services.GetRequiredService<InvitationSmtpProviderDataSource>();
            var owners = ((IEnumerable)Field(source, "_owners")).Cast<object>().ToArray();
            Require(owners.Length == 1 && !source.IsHealthy, "owner");
            Require(ReferenceEquals(Field(owners[0], "_retainedSmtp"), actual)
                && Field(owners[0], "_connection") is NpgsqlConnection { State: ConnectionState.Open }, "connector");
            stage = "refusal";
            var refused = await ExecuteAsync(executor, Guid.Parse(Required("LATER_JOB")));
            Require(refused.Status == JobExternalExecutionStatus.Reported
                && refused.Observation?.State == BackgroundJobExternalState.Unknown
                && ((IEnumerable)Field(source, "_owners")).Cast<object>().Count() == 1
                && client.DisposalAttempts == 1, "later_refusal");
            stage = "shutdown";
            var sourceDisposal = source.DisposeAsync().AsTask();
            // Dispose the actual API root service provider asynchronously. The
            // WebApplicationFactory wrapper also has synchronous disposal paths.
            var apiRoot = host.Services;
            // The live resolver may return DI's root scope rather than this provider object.
            Require(apiRoot.GetService(typeof(IServiceProvider)) is not null, "live_api_root");
            var hostDisposal = Task.Run(async () => await ((IAsyncDisposable)apiRoot).DisposeAsync());
            using (var disposalObservation = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                while (true)
                {
                    try { _ = apiRoot.GetService(typeof(IServiceProvider)); }
                    catch (ObjectDisposedException) { break; }
                    // Resolution refusal positively observes the actual DI root's
                    // closed admission; scheduling or elapsed waiting is not that proof.
                    await Task.Delay(10, disposalObservation.Token);
                }
            }
            Require(!sourceDisposal.IsCompleted && !hostDisposal.IsCompleted && client.IsConnected
                && Field(owners[0], "_connection") is NpgsqlConnection { State: ConnectionState.Open }, "retained_shutdown");
            Marker("retained-cleanup");
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            return 79;
        }
        catch (Exception error) when (error is not (OutOfMemoryException or StackOverflowException))
        {
            // Never expose provider/server/connection exception text or private inputs.
            Marker("failed-" + stage);
            return 80;
        }
    }

    private static async Task<JobExternalExecutionResult> ExecuteAsync(JobExecutor executor, Guid job)
    {
        var run = new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop());
        var start = await executor.ExecuteAsync(await ClaimSource.ClaimAsync(Required("DISPATCHER"), job), run);
        Require(start.Outcome == JobStartExecutionOutcome.Started, "start");
        var original = start.Receipt!;
        Require((await executor.ExecuteBusinessAsync(original,
            run.OpenBoundary(BackgroundJobCheckpointKind.Prepare))).CommitAcknowledged, "prepare");
        var read = await executor.ReadExternalStateAsync(original, CancellationToken.None);
        Require(read.Handle is not null, "read");
        var arm = await executor.RecordExternalArmAsync(original, read.Handle!,
            run.OpenBoundary(BackgroundJobCheckpointKind.Arm), CancellationToken.None);
        Require(arm.CommitAcknowledged, "arm");
        return await executor.ExecuteExternalAsync(read.Handle!, arm, CancellationToken.None);
    }

    private static object Field(object value, string name) => value.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(value)!;
    private static void Require(bool condition, string boundary)
    {
        if (!condition) throw new InvalidOperationException("Probe boundary refused: " + boundary);
    }
    private static void Marker(string value) { Protocol.WriteLine("InvitationSmtpProbe:" + value); Protocol.Flush(); }
    private static string Required(string name) => Environment.GetEnvironmentVariable("LEARNSTACK_SMTP_PROOF_" + name)
        ?? throw new InvalidOperationException("The SMTP proof environment is incomplete.");

    private sealed class AfterFinal250Provider(SmtpEmailProvider actual) : IEmailProvider
    {
        public async Task<EmailSubmissionObservation> SubmitAsync(EmailSubmission message, CancellationToken cancellationToken = default)
        {
            var observation = await actual.SubmitAsync(message, cancellationToken);
            Require(observation == new EmailSubmissionObservation(EmailSubmissionState.Accepted, true), "actual_final250");
            Marker("after-final250");
            // Parent kills this exact child after independently observing the committed Sending claim.
            await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
            return observation;
        }
    }
    private sealed class ProbeResilience : IProviderResilience<IEmailProvider>
    {
        public ResiliencePipeline Pipeline => ResiliencePipeline.Empty;
        public string PortName => "email";
    }
    private sealed class DisposalFailureClient : SmtpClient
    {
        internal int DisposalAttempts { get; private set; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { DisposalAttempts++; throw new InvalidOperationException("Owned SMTP disposal fault."); }
            base.Dispose(disposing);
        }
    }
    private sealed class ClaimSource : BackgroundJobRoutingClaim.MintingSource
    {
        internal static async Task<BackgroundJobRoutingClaim> ClaimAsync(string connectionString, Guid job)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                UPDATE background_jobs SET ownership_token=@token,ownership_kind='routing',
                    ownership_until=clock_timestamp()+interval '20 seconds' WHERE id=@job
                RETURNING id,tenant_id,organization_id,definition_key,queue,wake_generation,ownership_token,
                    ownership_until,original_due_at,profile,phase,clock_timestamp()
                """, connection);
            command.Parameters.AddWithValue("job", job); command.Parameters.AddWithValue("token", Guid.NewGuid());
            await using var row = await command.ExecuteReaderAsync();
            Require(await row.ReadAsync(), "routing");
            return MintClaim(row);
        }
    }
}
