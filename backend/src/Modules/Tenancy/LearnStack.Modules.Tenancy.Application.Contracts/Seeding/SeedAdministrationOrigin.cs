using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Seeding;

public sealed record SeedAdministrationOriginDeclaration(TenantId TenantId, string TenantSlug, string Host);

/// <summary>Only the fixed development owner supplies the two approved origin declarations.</summary>
public interface ISeedAdministrationOriginAdmission
{
    SeedAdministrationOriginDeclaration? ForTenant(TenantId tenantId);
}

public sealed class DenyAllSeedAdministrationOriginAdmission : ISeedAdministrationOriginAdmission
{
    public SeedAdministrationOriginDeclaration? ForTenant(TenantId tenantId) => null;
}

public sealed record AdoptSeedAdministrationOriginCommand(long ExpectedVersion) : IRequest<Result<long>>;

/// <summary>Actual locked target facts; possession alone grants no completion-read authority.</summary>
public sealed class SeedAdministrationOriginPreparedTarget(
    TenantId tenantId,
    string tenantSlug,
    long tenantVersion,
    int marker,
    OrganizationId? defaultOrganizationId,
    SeedAdministrationOriginPreparedMapping? mapping)
{
    public TenantId TenantId { get; } = tenantId;
    public string TenantSlug { get; } = tenantSlug;
    public long TenantVersion { get; } = tenantVersion;
    public int Marker { get; } = marker;
    public OrganizationId? DefaultOrganizationId { get; } = defaultOrganizationId;
    public SeedAdministrationOriginPreparedMapping? Mapping { get; } = mapping;
    public override string ToString() => nameof(SeedAdministrationOriginPreparedTarget);
}

/// <summary>The existing locked projection, or null on its target when absent.</summary>
public sealed class SeedAdministrationOriginPreparedMapping(
    string host,
    TenantId tenantId,
    OrganizationId? organizationId,
    bool isActive,
    bool isPubliclyLive)
{
    public string Host { get; } = host;
    public TenantId TenantId { get; } = tenantId;
    public OrganizationId? OrganizationId { get; } = organizationId;
    public bool IsActive { get; } = isActive;
    public bool IsPubliclyLive { get; } = isPubliclyLive;
    public override string ToString() => nameof(SeedAdministrationOriginPreparedMapping);
}

/// <summary>The fixed origin's completion proof on its already fenced, prepared owner.</summary>
public interface IFixedSeedOriginCompletionVerifier
{
    Task<bool> VerifyAsync(AdoptSeedAdministrationOriginCommand request,
        SeedAdministrationOriginPreparedTarget prepared, IdentityMutationLockPlan plan,
        CancellationToken cancellationToken = default);
}

public sealed class DenyAllFixedSeedOriginCompletionVerifier : IFixedSeedOriginCompletionVerifier
{
    public Task<bool> VerifyAsync(AdoptSeedAdministrationOriginCommand request,
        SeedAdministrationOriginPreparedTarget prepared, IdentityMutationLockPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

/// <summary>Owning-module pristine evidence, read afresh on the already fenced tenant owner.</summary>
public interface ISeedTenantContinuationGuard
{
    Task<bool> AllowsIdentityContinuationAsync(TenantId tenantId, CancellationToken cancellationToken = default);

    Task<bool> AllowsContentContinuationAsync(TenantId tenantId, bool requiresTheme,
        CancellationToken cancellationToken = default);
}
