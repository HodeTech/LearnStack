using System.Collections.Frozen;
using LearnStack.SharedKernel.Platform;

namespace LearnStack.Infrastructure.PlatformOperator;

/// <summary>What the operator source needs to know about one request type it may capture.</summary>
/// <param name="Operation">The registered operation that admits the request.</param>
public sealed record PlatformOperatorAct(PlatformOperation Operation);

/// <summary>
/// The closed set of request types an operator act may run, each bound to its operation (ADR-0102 § 2).
/// </summary>
/// <remarks>
/// Built once by the composition root from the five command types and never changed, so the source
/// and the executor judge the same list and a request outside it is never captured.
/// </remarks>
public sealed class PlatformOperatorActCatalog
{
    private readonly FrozenDictionary<Type, PlatformOperatorAct> _acts;

    /// <summary>Builds the catalog; every operation must be a registered one that needs no platform role.</summary>
    public PlatformOperatorActCatalog(IEnumerable<KeyValuePair<Type, PlatformOperatorAct>> acts)
    {
        ArgumentNullException.ThrowIfNull(acts);
        var list = acts.ToList();
        if (list.Count == 0
            || list.Any(pair => pair.Key.IsAbstract || !PlatformOperations.IsRegistered(pair.Value.Operation)
                || pair.Value.Operation.NeedsPlatformRole))
        {
            throw new ArgumentException("The operator act catalog needs registered operations that use the application role.");
        }

        _acts = list.ToFrozenDictionary();
    }

    /// <summary>The request types in the catalog.</summary>
    public IReadOnlyCollection<Type> RequestTypes => _acts.Keys;

    /// <summary>Finds the act a request type belongs to.</summary>
    public bool TryGet(Type requestType, out PlatformOperatorAct act)
    {
        ArgumentNullException.ThrowIfNull(requestType);
        return _acts.TryGetValue(requestType, out act!);
    }
}
