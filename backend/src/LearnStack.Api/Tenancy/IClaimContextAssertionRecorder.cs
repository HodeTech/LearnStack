using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Api.Tenancy;

/// <summary>Records only the fixed mismatch dimension under a previously confirmed claim context.</summary>
public interface IClaimContextAssertionRecorder
{
    Task RecordAsync(ITenantContext context, TenantAssertionDimension dimension);
}
