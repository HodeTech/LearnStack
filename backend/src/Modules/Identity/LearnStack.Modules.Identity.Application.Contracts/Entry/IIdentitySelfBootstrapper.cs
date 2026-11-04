using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Application.Contracts.Entry;

public enum IdentityCreationRefusal { Disabled = 1, WindowExhausted = 2, Unavailable = 3 }

public interface IIdentitySelfBootstrapper
{
    Task<IdentityBootstrapResult> BootstrapAsync(GlobalIdentitySubject subject, UserId? claimedActor = null,
        CancellationToken cancellationToken = default);
}

/// <summary>No refusal or unknown outcome supplies a stored actor or a success witness.</summary>
public sealed class IdentityBootstrapResult
{
    private IdentityBootstrapResult(IdentityBindingResult binding, IdentityCreationRefusal? refusal, int? retryAfterSeconds)
    {
        Binding = binding;
        Refusal = refusal;
        RetryAfterSeconds = retryAfterSeconds;
    }

    public IdentityBindingResult Binding { get; }
    public IdentityCreationRefusal? Refusal { get; }
    public int? RetryAfterSeconds { get; }

    public static IdentityBootstrapResult FromBinding(IdentityBindingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new(result, null, null);
    }

    public static IdentityBootstrapResult Refused(IdentityCreationRefusal reason, int? retryAfterSeconds = null)
    {
        if (!Enum.IsDefined(reason) || (reason == IdentityCreationRefusal.WindowExhausted
                ? retryAfterSeconds is not (>= 1 and <= 86400) : retryAfterSeconds is not null))
            throw new ArgumentException("Identity creation refusal is invalid.", nameof(reason));
        return new(IdentityBindingResult.Refused(IdentityBindingOutcome.AdmissionDenied), reason, retryAfterSeconds);
    }

    public override string ToString() => nameof(IdentityBootstrapResult);
}
