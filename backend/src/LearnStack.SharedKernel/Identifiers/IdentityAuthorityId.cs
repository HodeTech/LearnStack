using Vogen;

namespace LearnStack.SharedKernel.Identifiers;

/// <summary>The deployment-installed identity authority, independent of its current issuer address.</summary>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct IdentityAuthorityId : IStronglyTypedId<Guid>;
