using System.Collections.Frozen;
using LearnStack.SharedKernel.Persistence;

namespace LearnStack.Application.Persistence;

/// <summary>One immutable exact-type snapshot shared by all request scopes.</summary>
public sealed class ExplicitReadOnlyRequestCatalog(IEnumerable<ExplicitReadOnlyRequest> declarations)
{
    public FrozenSet<Type> RequestTypes { get; } = declarations.Select(declaration => declaration.RequestType).ToFrozenSet();
}
