using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Modules.Identity.Application.Abstractions;

public interface IRoleWriteStore : IAggregateWriteStore<Role, RoleId>
{
    /// <summary>Fresh tracked complete graph, including tombstones; repeated post-wait reads must not reuse stale tracked values.</summary>
    Task<Role?> FindAsync(RoleId roleId, CancellationToken cancellationToken = default);
    Task<bool> HasMembershipHistoryAsync(CancellationToken cancellationToken = default);
    Task ValidateGraphAsync(CancellationToken cancellationToken = default);
}

public interface IRoleReadStore
{
    Task<Role?> ReadAsync(RoleId roleId, CancellationToken cancellationToken = default);
}

public interface IMembershipWriteStore : IAggregateWriteStore<Membership, MembershipId>
{
    Task<Membership?> FindAsync(MembershipId membershipId, CancellationToken cancellationToken = default);
    Task<bool> HasMembershipHistoryAsync(CancellationToken cancellationToken = default);
    Task ValidateGraphAsync(CancellationToken cancellationToken = default);
}

public interface ISeedStateReader
{
    Task<RoleSeedDto?> ReadRoleAsync(RoleId roleId, CancellationToken cancellationToken = default);
    Task<MembershipSeedDto?> ReadMembershipAsync(MembershipId membershipId, CancellationToken cancellationToken = default);
}

public enum IdentityFoundationStorageFailure { Plan = 1, Target = 2, Invariant = 3, Graph = 4, Conflict = 5 }

/// <summary>Adapter-owned fixed diagnostic; never retains database text or inner exceptions.</summary>
public sealed class IdentityFoundationStorageException(IdentityFoundationStorageFailure failure)
    : Exception("Identity foundation storage refused the operation.")
{
    public IdentityFoundationStorageFailure Failure { get; } = failure;
}
