using LearnStack.Api.Tenancy;
using LearnStack.Application.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Api.Authentication;

internal sealed class ClaimContextAssertionRecorder(ConfirmedClaimAssertionSource source,
    Func<AuthorizationEvidenceCoordinator> evidence, ILogger<ClaimContextAssertionRecorder> logger) : IClaimContextAssertionRecorder
{
    public async Task RecordAsync(ITenantContext context, TenantAssertionDimension dimension)
    {
        var assertion = source.Capture(context, dimension switch
        {
            TenantAssertionDimension.Tenant => ClaimAssertionDimension.Tenant,
            TenantAssertionDimension.Organization => ClaimAssertionDimension.Organization,
            _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
        });
        if (assertion is null)
        {
            LogCaptureFailure(logger, null);
            return;
        }
        await evidence().RecordConfirmedAssertionAsync(assertion);
    }

    private static readonly Action<ILogger, Exception?> LogCaptureFailure = LoggerMessage.Define(
        LogLevel.Critical, new EventId(1, "ClaimContextAssertionCaptureFailed"),
        "Confirmed claim assertion evidence could not be captured. The request remains refused.");
}
