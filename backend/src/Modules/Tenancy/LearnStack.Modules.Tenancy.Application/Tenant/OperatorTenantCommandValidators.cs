using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;

namespace LearnStack.Modules.Tenancy.Application.Tenant;

/// <summary>
/// The operator's provisioning command refuses exactly what the seed's does: the same shared rules, run
/// on the same fields, so a refusal reaches the operator in the same Problem Details shape and the two
/// cannot drift.
/// </summary>
internal sealed class OperatorProvisionTenantCommandValidator : AbstractValidator<OperatorProvisionTenantCommand>
{
    private static readonly ProvisionTenantCommandValidator Rules = new();

    public OperatorProvisionTenantCommandValidator() =>
        RuleFor(command => command).Custom((command, context) =>
        {
            var result = Rules.Validate(new ProvisionTenantCommand(
                command.TenantId, command.Slug, command.DisplayName, command.DefaultOrganizationId,
                command.DefaultOrganizationSlug, command.DefaultOrganizationDisplayName));
            foreach (var failure in result.Errors)
            {
                context.AddFailure(failure);
            }
        });
}

/// <summary>The operator's host-mapping command refuses what the seed's does, for the same reasons.</summary>
internal sealed class OperatorMapHostCommandValidator : AbstractValidator<OperatorMapHostCommand>
{
    private static readonly MapHostToTenantCommandValidator Rules = new();

    public OperatorMapHostCommandValidator() =>
        RuleFor(command => command).Custom((command, context) =>
        {
            var result = Rules.Validate(new MapHostToTenantCommand(
                command.Host, command.OrganizationId, command.IsActive, command.IsPubliclyLive));
            foreach (var failure in result.Errors)
            {
                context.AddFailure(failure);
            }
        });
}
