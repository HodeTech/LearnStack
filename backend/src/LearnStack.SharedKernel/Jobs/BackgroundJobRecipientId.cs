using LearnStack.SharedKernel.Identifiers;
using Vogen;

namespace LearnStack.SharedKernel.Jobs;

/// <summary>One occurrence's immutable tenant route, minted through IGuidFactory.NewUuidV7.</summary>
[ValueObject<Guid>(LearnStackVogenDefaults.IdMask)]
public readonly partial record struct BackgroundJobRecipientId : IStronglyTypedId<Guid>;
