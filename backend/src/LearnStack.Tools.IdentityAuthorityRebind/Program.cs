using LearnStack.Modules.Identity.Infrastructure.Bindings;
using Microsoft.Extensions.Configuration;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using LearnStack.Infrastructure.PlatformOperator;

namespace LearnStack.Tools.IdentityAuthorityRebind;

/// <summary>
/// The controlled issuer rebind tool (ADR-0104). It takes every value from its environment and one line of standard
/// input, never an argument, and prints one fixed line. See <c>docs/runbooks/identity-issuer-rebind.md</c>.
/// </summary>
internal static class Program
{
    private const string UsageRefusal = "rebind: outcome=refused_input";
    private static readonly TimeSpan Deadline = TimeSpan.FromMinutes(10);

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 0)
        {
            Console.Error.WriteLine(UsageRefusal);
            return RebindTool.Refused;
        }

        try
        {
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
            if (!RebindToolInput.TryRead(
                    name => Environment.GetEnvironmentVariable(name),
                    () => Console.IsInputRedirected ? Console.In.ReadLine() : null,
                    configuration,
                    out var input))
            {
                Console.Error.WriteLine(UsageRefusal);
                return RebindTool.Refused;
            }

            using var meters = input!.OtlpEndpoint is null
                ? null
                : Sdk.CreateMeterProviderBuilder()
                    .AddMeter(IdentityRebindMetrics.MeterName, PlatformOperatorMetrics.MeterName)
                    .AddOtlpExporter()
                    .Build();
            using var deadline = new CancellationTokenSource(Deadline);
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
            Console.Error.WriteLine("rebind: outcome=refused_unexpected");
            return RebindTool.Refused;
        }
    }
}
