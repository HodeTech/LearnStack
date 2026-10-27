using System.Text.Json.Serialization;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Contracts.Memberships;

[JsonConverter(typeof(JsonStringEnumConverter<DirectoryMembershipStatus>))]
public enum DirectoryMembershipStatus { Active = 1, Suspended = 2 }

/// <summary>The complete administrative directory projection; provider and global profile fields are excluded.</summary>
public sealed record MembershipDirectoryItem(Guid MembershipId, UserId UserId, OrganizationId? OrganizationId,
    DirectoryMembershipStatus Status, [property: PiiSensitive] string? DisplayName,
    [property: PiiSensitive] string? PreferredLocale);

public sealed record ListMembershipsQuery(string? Cursor = null, int Limit = CursorPagination.DefaultLimit)
    : IRequest<Result<Page<MembershipDirectoryItem>>>;
