using FluentValidation;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Domain;

namespace LearnStack.Modules.Identity.Application.Federation;

public sealed class BeginConfigureFederationCommandValidator : AbstractValidator<BeginConfigureFederationCommand>
{
    public BeginConfigureFederationCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).Must(version => version is null or >= 0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.Protocol).IsInEnum().WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Source).IsInEnum().WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ConfigurationDigest).Must(FederationConnection.IsDigest).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.IssuerOrEntityId).Must(value => FederationInput.VisibleAscii(value, FederationConnection.MaximumIssuerLength))
            .WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ClientId).Must(value => value is null || FederationInput.VisibleAscii(value, FederationConnection.MaximumClientIdLength))
            .WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.DisplayName).Must(value => value is null || value.Length <= FederationDisplayName.MaximumLength * 4)
            .WithErrorCode("lockey_invalid_value");
    }
}

public sealed class BeginRotateFederationSecretCommandValidator : AbstractValidator<BeginRotateFederationSecretCommand>
{
    public BeginRotateFederationSecretCommandValidator() =>
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
}

public sealed class BeginRenameFederationCommandValidator : AbstractValidator<BeginRenameFederationCommand>
{
    public BeginRenameFederationCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.DisplayName).Must(value => value is { Length: > 0 } && value.Length <= FederationDisplayName.MaximumLength * 4)
            .WithErrorCode("lockey_invalid_value");
    }
}

public sealed class BeginFederationTestStartCommandValidator : AbstractValidator<BeginFederationTestStartCommand>
{
    public BeginFederationTestStartCommandValidator() =>
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
}

public sealed class BeginDisableFederationCommandValidator : AbstractValidator<BeginDisableFederationCommand>
{
    public BeginDisableFederationCommandValidator() =>
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
}

public sealed class BeginDeleteFederationCommandValidator : AbstractValidator<BeginDeleteFederationCommand>
{
    public BeginDeleteFederationCommandValidator() =>
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
}

public sealed class ResumeFederationOperationCommandValidator : AbstractValidator<ResumeFederationOperationCommand>
{
    public ResumeFederationOperationCommandValidator() =>
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
}

public sealed class EnableFederationCommandValidator : AbstractValidator<EnableFederationCommand>
{
    public EnableFederationCommandValidator() =>
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
}

public sealed class CompleteFederationOperationCommandValidator : AbstractValidator<CompleteFederationOperationCommand>
{
    public CompleteFederationOperationCommandValidator()
    {
        RuleFor(request => request.ConnectionId).Must(FederationInput.ValidId).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.OperationId).Must(FederationInput.ValidId).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Outcome).IsInEnum().WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ProviderDigest).Must(value => value is null || FederationConnection.IsDigest(value))
            .WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.Reason).Must(value => value is null || Enum.IsDefined(value.Value)).WithErrorCode("lockey_invalid_value");
    }
}

public sealed class ActivateFederationCommandValidator : AbstractValidator<ActivateFederationCommand>
{
    public ActivateFederationCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.ProviderDigest).Must(FederationConnection.IsDigest).WithErrorCode("lockey_invalid_value");
    }
}

public sealed class RecordFederationTestResultCommandValidator : AbstractValidator<RecordFederationTestResultCommand>
{
    public RecordFederationTestResultCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.TestAttemptId).Must(FederationInput.ValidId).WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ProviderDigest).Must(value => value is null || FederationConnection.IsDigest(value))
            .WithErrorCode("lockey_invalid_value");
    }
}

public sealed class ReconcileFederationConnectionCommandValidator : AbstractValidator<ReconcileFederationConnectionCommand>
{
    public ReconcileFederationConnectionCommandValidator()
    {
        RuleFor(request => request.ExpectedVersion).GreaterThanOrEqualTo(0).WithErrorCode("lockey_concurrency_conflict");
        RuleFor(request => request.ProviderDigest).Must(value => value is null || FederationConnection.IsDigest(value))
            .WithErrorCode("lockey_invalid_value");
        RuleFor(request => request.ProviderConfigurationDigest).Must(value => value is null || FederationConnection.IsDigest(value))
            .WithErrorCode("lockey_invalid_value");
    }
}

internal static class FederationInput
{
    internal static bool ValidId(Guid value) => value != Guid.Empty && value.Version == 7;
    internal static bool VisibleAscii(string? value, int maximum) => value is { Length: > 0 } text && text.Length <= maximum
        && text.All(character => character is >= '!' and <= '~');
}
