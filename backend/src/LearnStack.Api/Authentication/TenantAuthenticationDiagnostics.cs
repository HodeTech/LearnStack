using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace LearnStack.Api.Authentication;

/// <summary>Suppress vendor failure text before sinks; emit closed outcomes instead.</summary>
internal static class TenantAuthenticationDiagnostics
{
    internal const string VendorCategory =
        "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler";

    internal static bool IsVendorEvent(LogEvent entry) =>
        entry.Properties.TryGetValue("SourceContext", out var source)
        && source is ScalarValue { Value: VendorCategory };

    internal static void Outcome(ILogger logger, string stage, string reason) =>
        AuthenticationOutcome(logger, stage, reason, null);

    private static readonly Action<ILogger, string, string, Exception?> AuthenticationOutcome =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, "TenantAuthenticationOutcome"),
            "Tenant authentication stage {Stage} outcome {Reason}.");
}
