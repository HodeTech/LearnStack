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
    private const string InputRefusal = "rebind: outcome=refused_input";
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan StandardInputWait = TimeSpan.FromSeconds(30);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            Console.Out.WriteLine(InputRefusal);
            return RebindTool.Refused;
        }

        try
        {
            using var deadline = new CancellationTokenSource(Deadline);

            // An interrupt ends the run the way the deadline does: through the token, so the run rolls back, the
            // commit's outcome is still reconciled, and the one line is still printed.
            Console.CancelKeyPress += (_, signal) =>
            {
                signal.Cancel = true;
                deadline.Cancel();
            };
            using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                deadline.Cancel();
            });

            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            if (!RebindToolInput.TryRead(
                    name => Environment.GetEnvironmentVariable(name),
                    ReadStandardInputLine,
                    configuration,
                    out var input))
            {
                Console.Out.WriteLine(InputRefusal);
                return RebindTool.Refused;
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
            Console.Out.WriteLine("rebind: outcome=refused_unexpected");
            return RebindTool.Refused;
        }
    }

    /// <summary>
    /// The operator token's line when it is piped: bounded in time, so a pipe that is never closed refuses the run
    /// instead of holding it before its deadline exists.
    /// </summary>
    private static string? ReadStandardInputLine()
    {
        if (!Console.IsInputRedirected)
        {
            return null;
        }

        var read = Task.Run(Console.In.ReadLine);
        return read.Wait(StandardInputWait) ? read.Result : null;
    }
}
