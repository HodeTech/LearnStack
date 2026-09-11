using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Ghost.Domain;

/// <summary>
/// An aggregate root in a module namespace no spec directory exists for — the fixture
/// <c>AuditCoverageTests.The_Module_Sweep_Can_Actually_Fail</c> feeds its discovery.
/// </summary>
/// <remarks>
/// It lives in the architecture test assembly, which no discovery sweeps, and in a
/// namespace of its own because the rule reads a module's name from its types' namespaces:
/// a probe declared in the suite's own namespace could not show the aggregate half working.
/// </remarks>
internal sealed class GhostAggregate : IAggregateRoot<GhostId>
{
    public GhostId Id { get; } = new(Guid.CreateVersion7());
}

internal readonly record struct GhostId(Guid Value) : IStronglyTypedId<Guid>
{
    public bool IsInitialized() => Value != Guid.Empty;
}
