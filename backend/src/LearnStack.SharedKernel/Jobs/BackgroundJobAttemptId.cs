using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.SharedKernel.Jobs;

/// <summary>One counted normal or finalize admission, minted through IGuidFactory.NewUuidV7.</summary>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct BackgroundJobAttemptId : IStronglyTypedId<Guid>;
