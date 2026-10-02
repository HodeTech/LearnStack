using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Branding;

/// <summary>The setting identity, committed root version and canonical complete palette.</summary>
public sealed record TenantBrandingDto(Guid SettingId, long Version, string Theme);

/// <summary>Unrouted whole-theme write in the trusted context's tenant-wide scope.</summary>
/// <param name="SettingId">Explicit identity; no lookup or replacement by display name.</param>
/// <param name="Theme">Complete closed palette, admitted for safe colors and contrast.</param>
/// <param name="ExpectedVersion">Null is create-only; an exact version is replace-only.</param>
public sealed record SetTenantBrandingCommand(Guid SettingId, string Theme, long? ExpectedVersion)
    : IRequest<Result<TenantBrandingDto>>;
