using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Branding;

public sealed record TenantBrandingDto(Guid SettingId, long Version, string Theme);

// Null expected version means create-only; an exact version means replace-only.
public sealed record SetTenantBrandingCommand(Guid SettingId, string Theme, long? ExpectedVersion)
    : IRequest<Result<TenantBrandingDto>>;
