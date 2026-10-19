using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.SharedKernel.Jobs;

/// <summary>One durable logical recurrence slot, minted through IGuidFactory.NewUuidV7.</summary>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct BackgroundJobOccurrenceId : IStronglyTypedId<Guid>;
