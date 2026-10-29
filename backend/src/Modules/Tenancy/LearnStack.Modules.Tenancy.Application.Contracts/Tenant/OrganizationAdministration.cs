using System.Collections.Immutable;
using System.Text.Json.Serialization;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Tenant;

[JsonConverter(typeof(JsonStringEnumConverter<ManagedOrganizationStatus>))]
public enum ManagedOrganizationStatus { Active = 1, Suspended = 2, Archived = 3 }

/// <summary>The complete bounded organization directory projection.</summary>
public sealed record OrganizationDirectoryItem(OrganizationId OrganizationId, string Slug,
    [property: PiiSensitive] string DisplayName, ManagedOrganizationStatus Status, bool IsDefault, long Version)
{
    public override string ToString() => nameof(OrganizationDirectoryItem);
}

/// <summary>Organization page and the separately versioned Tenant default reference.</summary>
public sealed record OrganizationDirectoryPage(ImmutableArray<OrganizationDirectoryItem> Items,
    OrganizationId? DefaultOrganizationId, long TenantVersion, PageInfo PageInfo)
{
    public override string ToString() => nameof(OrganizationDirectoryPage);
}

public sealed record OrganizationDetailDto(OrganizationId OrganizationId, string Slug,
    [property: PiiSensitive] string DisplayName, ManagedOrganizationStatus Status, bool IsDefault, long Version)
{
    public override string ToString() => nameof(OrganizationDetailDto);
}

/// <summary>Exact changed root retained internally for protected receipt disclosure.</summary>
public sealed record OrganizationMutationResult(OrganizationId OrganizationId, long Version);

public sealed record ListOrganizationsQuery(string? Cursor = null, int Limit = CursorPagination.DefaultLimit)
    : IRequest<Result<OrganizationDirectoryPage>>
{
    public override string ToString() => nameof(ListOrganizationsQuery);
}

public sealed record GetOrganizationDetailQuery(OrganizationId OrganizationId)
    : IRequest<Result<OrganizationDetailDto>>;

/// <summary>Creates an organization with a server-assigned identity in the current orgless tenant.</summary>
public sealed record CreateManagedOrganizationCommand([property: PiiSensitive] string Slug,
    [property: PiiSensitive] string DisplayName)
    : IRequest<Result<OrganizationMutationResult>>
{
    public override string ToString() => nameof(CreateManagedOrganizationCommand);
}

public sealed record RenameOrganizationCommand(OrganizationId OrganizationId, long ExpectedVersion,
    [property: PiiSensitive] string DisplayName) : IRequest<Result<OrganizationMutationResult>>
{
    public override string ToString() => nameof(RenameOrganizationCommand);
}

public sealed record ArchiveOrganizationCommand(OrganizationId OrganizationId, long ExpectedVersion)
    : IRequest<Result<OrganizationMutationResult>>;

/// <summary>The precondition and result version belong to Tenant, whose default reference changes.</summary>
public sealed record SelectDefaultOrganizationCommand(OrganizationId OrganizationId, long ExpectedVersion)
    : IRequest<Result<long>>;
