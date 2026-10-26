using LearnStack.SharedKernel;
using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.Modules.Identity.Domain;

[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct MembershipId : IStronglyTypedId<Guid>;

[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct RoleId : IStronglyTypedId<Guid>;
