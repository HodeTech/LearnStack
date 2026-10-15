using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Contracts.Customization;

/// <summary>Creates Active card@1 only when the ambient tenant has no card history.</summary>
public sealed record InitializeTenantContentTypeDefaultCommand : IRequest<Result<None>>;

/// <summary>Creates Active plain@1 only when the ambient tenant has no plain history.</summary>
public sealed record InitializeTenantLevelTaxonomyDefaultCommand : IRequest<Result<None>>;
