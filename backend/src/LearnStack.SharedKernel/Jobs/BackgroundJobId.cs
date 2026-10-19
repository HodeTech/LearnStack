using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.SharedKernel.Jobs;

/// <summary>One durable job intent; minted through IGuidFactory.NewUuidV7, never a vendor ID.</summary>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct BackgroundJobId : IStronglyTypedId<Guid>;
