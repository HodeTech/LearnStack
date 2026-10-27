using Microsoft.Extensions.Logging;
using Serilog.Events;

namespace LearnStack.Api.Authentication;

internal enum TenantAuthenticationOutcome
{
    CredentialRejected,
    ProfileRejected,
    ValidationAccepted,
    ValidationRejected,
    BindingStoreUnavailable,
}

/// <summary>Suppress vendor failure text before sinks; emit closed outcomes instead.</summary>
internal static class TenantAuthenticationDiagnostics
{
    internal const string VendorCategory =
        "Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerHandler";

    internal static bool IsVendorEvent(LogEvent entry) =>
        entry.Properties.TryGetValue("SourceContext", out var source)
        && source is ScalarValue { Value: VendorCategory };

    internal static void Outcome(ILogger logger, TenantAuthenticationOutcome outcome)
    {
        var (stage, reason) = outcome switch
        {
            TenantAuthenticationOutcome.CredentialRejected => ("credential", "rejected"),
            TenantAuthenticationOutcome.ProfileRejected => ("profile", "rejected"),
            TenantAuthenticationOutcome.ValidationAccepted => ("validation", "accepted"),
            TenantAuthenticationOutcome.ValidationRejected => ("validation", "rejected"),
            TenantAuthenticationOutcome.BindingStoreUnavailable => ("binding", "binding_store_unavailable"),
            _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
        };
        AuthenticationOutcome(logger, stage, reason, null);
    }

    private static readonly Action<ILogger, string, string, Exception?> AuthenticationOutcome =
        LoggerMessage.Define<string, string>(
            LogLevel.Information,
            new EventId(1, "TenantAuthenticationOutcome"),
            "Tenant authentication stage {Stage} outcome {Reason}.");
}
