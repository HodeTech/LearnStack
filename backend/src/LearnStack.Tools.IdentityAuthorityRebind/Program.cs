using System.Runtime.InteropServices;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace LearnStack.Tools.IdentityAuthorityRebind;

/// <summary>
/// The controlled issuer rebind tool (ADR-0104). It takes every value from its environment and one line of standard
/// input, never an argument, and prints one fixed line to standard output. See
/// <c>docs/runbooks/identity-issuer-rebind.md</c>.
/// </summary>
internal static class Program
{
    private static readonly TimeSpan StandardInputWait = TimeSpan.FromSeconds(30);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            return RebindTool.RefuseInput(Console.Out, RebindToolInput.Fields.Arguments);
        }

        // The signals that end a run from outside: an interrupt, a terminate, and the hang-up an SSH session that
        // drops sends. Each ends it the way the deadline does: through the token, so the run rolls back, the commit's
        // outcome is still reconciled, and the one line is still printed.
        using var deadline = new CancellationTokenSource(IdentityIssuerRebind.RunDeadline);
        ConsoleCancelEventHandler interrupt = (_, signal) =>
        {
            signal.Cancel = true;
            Cancel(deadline);
        };
        Console.CancelKeyPress += interrupt;
        using var terminate = Register(PosixSignal.SIGTERM, deadline);
        using var hangUp = Register(PosixSignal.SIGHUP, deadline);

        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            if (!RebindToolInput.TryRead(
                    name => Environment.GetEnvironmentVariable(name),
                    () => ReadStandardInputLine(deadline.Token),
                    configuration,
                    out var input,
                    out var field))
            {
                return deadline.IsCancellationRequested
                    ? RebindTool.Refuse(Console.Out, "cancelled")
                    : RebindTool.RefuseInput(Console.Out, field);
            }

            using var meters = input!.OtlpEndpoint is null
                ? null
                : Sdk.CreateMeterProviderBuilder()
                    .AddMeter(IdentityRebindMetrics.MeterName, PlatformOperatorMetrics.MeterName)
                    .AddOtlpExporter()
                    .Build();
            using var client = KeycloakRealmContinuitySource.CreateClient();
            var realm = new KeycloakRealmContinuitySource(client, input.RealmClientId, input.RealmClientSecret);
            var exit = await RebindTool.RunAsync(input, realm, Console.Out, deadline.Token);
            meters?.ForceFlush(5000);
            return exit;
        }
#pragma warning disable CA1031 // The tool emits one fixed line, never an exception payload that could carry a credential.
        catch (Exception)
#pragma warning restore CA1031
        {
            return RebindTool.Refuse(Console.Out, "unexpected");
        }
        finally
        {
            Console.CancelKeyPress -= interrupt;
        }
    }

    private static PosixSignalRegistration Register(PosixSignal signal, CancellationTokenSource deadline) =>
        PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            Cancel(deadline);
        });

    /// <summary>A signal can arrive in the instant the run is ending; it must not fault the process then.</summary>
    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// The operator token's line when it is piped: bounded in time and ended by a signal, so a pipe that is never
    /// closed refuses the run instead of holding it before its deadline exists.
    /// </summary>
    private static string? ReadStandardInputLine(CancellationToken cancellationToken)
    {
        if (!Console.IsInputRedirected)
        {
            return null;
        }

        var read = Task.Run(Console.In.ReadLine, CancellationToken.None);
        try
        {
            return read.Wait(StandardInputWait, cancellationToken) ? read.Result : null;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
