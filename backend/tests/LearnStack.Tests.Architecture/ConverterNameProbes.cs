namespace LearnStack.Tests.Architecture.Probes;

/// <summary>
/// Borrows the names Vogen gives its EF Core types without being a value object, for
/// <c>The_Domain_EF_Core_Rule_Can_Actually_Fail</c>. Never constructed.
/// </summary>
internal sealed class NotAnId
{
    /// <summary>The nested converter's name, inside a type that is not a value object.</summary>
    internal sealed class EfCoreValueConverter;
}

/// <summary>
/// The extensions class's name, for a type that is not a value object — for
/// <c>The_Domain_EF_Core_Rule_Can_Actually_Fail</c>.
/// </summary>
internal static class __NotAnIdEfCoreExtensions;

/// <summary>
/// A value object with a hand-written nested type whose name is close to an emitted one.
/// Vogen emits exactly two — <c>EfCoreValueConverter</c> and <c>EfCoreValueComparer</c> — so
/// a third is written by hand however it is spelled. Inert: this project references Vogen's
/// attributes, not its generator.
/// </summary>
[Vogen.ValueObject<Guid>]
internal readonly partial struct BorrowedNameProbeId
{
    internal sealed class EfCoreValueReader;
}
