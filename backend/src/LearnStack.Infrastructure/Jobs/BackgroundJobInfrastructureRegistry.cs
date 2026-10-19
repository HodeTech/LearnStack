using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Entitlements;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

public enum BackgroundJobWorkerPoolKind
{
    Transactional,
    External,
    Control,
}

/// <summary>Pool and queue names both use [a-z][a-z0-9_]{0,49}; queues belong to exactly one pool.</summary>
public sealed class BackgroundJobWorkerPoolRegistration
{
    public BackgroundJobWorkerPoolRegistration(string name, BackgroundJobWorkerPoolKind kind,
        int workers, IEnumerable<string> queues)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Kind = kind;
        Workers = workers;
        ArgumentNullException.ThrowIfNull(queues);
        Queues = Array.AsReadOnly(queues.ToArray());
    }

    public string Name { get; }
    public BackgroundJobWorkerPoolKind Kind { get; }
    public int Workers { get; }
    public IReadOnlyList<string> Queues { get; }
}

/// <summary>API composition binds a Contracts port to its own module's concrete Infrastructure adapter.</summary>
public sealed record BackgroundJobExternalAdapterRegistration(Type PortType, Type ImplementationType);

/// <summary>Frozen construction-free topology, capacity declarations and external adapter bindings.</summary>
public sealed class BackgroundJobInfrastructureRegistry
{
    private readonly FrozenDictionary<string, BackgroundJobWorkerPoolRegistration> _queues;
    private readonly FrozenDictionary<Type, BackgroundJobExternalAdapterRegistration> _adapters;

    private BackgroundJobInfrastructureRegistry(
        FrozenDictionary<string, BackgroundJobWorkerPoolRegistration> pools,
        FrozenDictionary<string, BackgroundJobWorkerPoolRegistration> queues,
        FrozenDictionary<string, BackgroundJobCapacityGroupRegistration> capacityGroups,
        FrozenDictionary<Type, BackgroundJobExternalAdapterRegistration> adapters)
    {
        Pools = pools;
        _queues = queues;
        CapacityGroups = capacityGroups;
        _adapters = adapters;
        QueueNames = Array.AsReadOnly(queues.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyDictionary<string, BackgroundJobWorkerPoolRegistration> Pools { get; }
    public IReadOnlyDictionary<string, BackgroundJobCapacityGroupRegistration> CapacityGroups { get; }
    public IReadOnlyList<string> QueueNames { get; }

    public static BackgroundJobInfrastructureRegistry CreateDefault() => Create(
        [new("transactional", BackgroundJobWorkerPoolKind.Transactional, 1, ["transactional"]),
            new("external", BackgroundJobWorkerPoolKind.External, 2, ["external"]),
            new("control", BackgroundJobWorkerPoolKind.Control, 1, ["control"])], [], [], []);

    public static BackgroundJobInfrastructureRegistry Create(
        IEnumerable<BackgroundJobWorkerPoolRegistration> pools,
        IEnumerable<BackgroundJobCapacityGroupRegistration> capacityGroups,
        IEnumerable<BackgroundJobExternalAdapterRegistration> adapters,
        IEnumerable<BackgroundJobModuleRegistration> knownModules)
    {
        ArgumentNullException.ThrowIfNull(pools);
        ArgumentNullException.ThrowIfNull(capacityGroups);
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(knownModules);
        var modules = knownModules.ToArray();
        if (modules.Any(module => module is null)
            || modules.Select(module => module.ModuleName).Distinct(StringComparer.Ordinal).Count() != modules.Length)
            throw Invalid("Job modules must be distinct and non-null.");
        var byName = new Dictionary<string, BackgroundJobWorkerPoolRegistration>(StringComparer.Ordinal);
        var byQueue = new Dictionary<string, BackgroundJobWorkerPoolRegistration>(StringComparer.Ordinal);
        foreach (var pool in pools)
        {
            if (pool is null || !BackgroundJobNames.QueueOrPool(pool.Name) || !Enum.IsDefined(pool.Kind)
                || pool.Workers is < 1 or > 16 || pool.Queues.Count == 0 || !byName.TryAdd(pool.Name, pool))
                throw Invalid("Job pools require distinct finite names, a declared kind and 1–16 workers.");
            foreach (var queue in pool.Queues)
                if (!BackgroundJobNames.QueueOrPool(queue) || !byQueue.TryAdd(queue, pool))
                    throw Invalid("A valid capability queue belongs to exactly one pool.");
        }
        if (byName.Count is < 3 or > 8 || byQueue.Count > 64 || byName.Values.Sum(pool => pool.Workers) > 32
            || Enum.GetValues<BackgroundJobWorkerPoolKind>().Any(kind => !byName.Values.Any(pool => pool.Kind == kind)))
            throw Invalid("Job topology requires transactional, external and control pools within the 8/64/32 bounds.");

        var groups = new Dictionary<string, BackgroundJobCapacityGroupRegistration>(StringComparer.Ordinal);
        foreach (var group in capacityGroups)
        {
            var module = group is null ? null : modules.SingleOrDefault(candidate => candidate.Application == group.DeclaringType.Assembly);
            if (group is null || module is null || !BackgroundJobNames.ModuleResource(group.Key)
                || !group.Key.StartsWith(module.ModuleName + ".", StringComparison.Ordinal)
                || group.OperationalCeiling is < 1 or > 1024 || !groups.TryAdd(group.Key, group)
                || group.PlanLimit is { } limit
                    && (!LimitKeys.All.TryGetValue(limit, out var descriptor) || descriptor.Enforcement != LimitEnforcement.Hard))
                throw Invalid("A capacity group belongs to a known module and uses a positive ceiling and optional declared Hard limit.");
        }

        var byPort = new Dictionary<Type, BackgroundJobExternalAdapterRegistration>();
        foreach (var adapter in adapters)
        {
            var module = adapter?.PortType is { } port ? modules.SingleOrDefault(candidate => candidate.Contracts == port.Assembly) : null;
            if (adapter is null || module is null || !adapter.PortType.IsInterface || adapter.PortType.ContainsGenericParameters
                || adapter.ImplementationType is not { IsClass: true, IsAbstract: false, ContainsGenericParameters: false } implementation
                || implementation.Assembly != module.Infrastructure || !adapter.PortType.IsAssignableFrom(implementation)
                || !byPort.TryAdd(adapter.PortType, adapter))
                throw Invalid("External adapters bind a known module Contracts port to its concrete Infrastructure implementation.");
            var contracts = adapter.PortType.GetInterfaces();
            if (contracts.Length != 1 || !contracts[0].IsGenericType
                || contracts[0].GetGenericTypeDefinition() != typeof(IBackgroundJobExternalOperation<,>)
                || adapter.PortType.GetMethods().Length != 0)
                throw Invalid("An external port inherits exactly one closed Execute/Reconcile contract and declares no extra methods.");
        }

        return new(byName.ToFrozenDictionary(StringComparer.Ordinal), byQueue.ToFrozenDictionary(StringComparer.Ordinal),
            groups.ToFrozenDictionary(StringComparer.Ordinal), byPort.ToFrozenDictionary());
    }

    public bool TryGetPoolForQueue(string queue, [NotNullWhen(true)] out BackgroundJobWorkerPoolRegistration? pool) =>
        _queues.TryGetValue(queue, out pool);

    public bool TryGetAdapter(Type port, [NotNullWhen(true)] out BackgroundJobExternalAdapterRegistration? adapter) =>
        _adapters.TryGetValue(port, out adapter);

    private static ArgumentException Invalid(string message) => new(message);
}
