namespace LearnStack.SharedKernel.Errors;

/// <summary>A fixed failure contract without provider details or an inner exception.</summary>
public sealed class ConfidentialCommitProviderException()
    : ProviderException("custom-field-commit", "Custom field storage commit failed.", isClientError: false);
