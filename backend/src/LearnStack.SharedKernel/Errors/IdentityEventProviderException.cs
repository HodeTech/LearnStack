namespace LearnStack.SharedKernel.Errors;

public enum IdentityEventProviderFailure { Unavailable, Credentials, Policy, Capacity }

/// <summary>Closed operational classification; no provider exception/body/credential is retained.</summary>
public sealed class IdentityEventProviderException : ProviderException
{
    public IdentityEventProviderException(IdentityEventProviderFailure failure, bool isClientError = false)
        : base("keycloak", "Retained identity event source is unavailable.", isClientError)
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        Failure = failure;
    }
    public IdentityEventProviderFailure Failure { get; }
}
