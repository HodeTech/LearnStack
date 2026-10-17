using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Results;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Canonical facts from the exact successful Prepare response, never a caller-selected Value accessor.</summary>
internal sealed record BackgroundJobPreparedPlan(string Json, string Fingerprint, Guid OperationId)
{
    internal static BackgroundJobPreparedPlan Capture(IResultBase response, Type registeredPlanType, BackgroundJobId jobId)
    {
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(registeredPlanType);
        BackgroundJobPayloadContract.ValidateShape(registeredPlanType, BackgroundJobPayloadKind.Plan);
        if (!response.IsSuccess || response.GetType() != typeof(Result<>).MakeGenericType(registeredPlanType))
            throw new InvalidOperationException("Prepare must return the exact registered successful Result<Plan>.");
        // A single closed bridge reads Result<TPlan>.Value. IResultBase gains no
        // payload surface, and neither handlers nor callers supply a callback.
        try
        {
            return (BackgroundJobPreparedPlan)typeof(BackgroundJobPreparedPlan)
                .GetMethod(nameof(CaptureTyped), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(registeredPlanType).Invoke(null, [response, jobId])!;
        }
        catch (TargetInvocationException wrapped) when (wrapped.InnerException is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
    }

    private static BackgroundJobPreparedPlan CaptureTyped<TPlan>(IResultBase response, BackgroundJobId jobId)
        where TPlan : IBackgroundJobPlan
    {
        var typed = (Result<TPlan>)response;
        if (!typed.IsSuccess) throw new InvalidOperationException("A failed Prepare has no committed plan.");
        var json = BackgroundJobPayloadContract.Serialize(typed.Value, typeof(TPlan), BackgroundJobPayloadKind.Plan);
        var fingerprint = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        return new(json, fingerprint, BackgroundJobExternalIdentity.OperationId(jobId, fingerprint));
    }
}
