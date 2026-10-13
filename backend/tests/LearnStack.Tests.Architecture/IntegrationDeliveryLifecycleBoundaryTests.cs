using System.Data;
using System.Data.Common;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string LifecycleUnit = "LearnStack.SharedKernel.Persistence.IUnitOfWork";
    private const string LifecycleScope = "LearnStack.SharedKernel.Persistence.IUnitOfWorkScope";
    private const string LifecycleAdapter = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
    private const string LifecycleFrame = LifecycleAdapter + "/Frame";
    private const string LifecycleAcceptance = "LearnStack.Infrastructure.Messaging.PostgresDeliveryAcceptance";
    private const string LifecyclePlatform = "LearnStack.Infrastructure.MultiTenancy.PlatformAdminScope";
    private const string LifecycleTask = "System.Threading.Tasks.Task";
    private const string LifecycleScopeTask = "System.Threading.Tasks.Task`1<" + LifecycleScope + ">";
    private static readonly string[] LifecycleTerminalMethods = ["Commit", "Rollback", "CommitAsync", "RollbackAsync"];

    /// <summary>
    /// ADR-0046's completion gate and ADR-0057's common owner: typed unit/frame and
    /// database transaction completion stay with the enumerated owners. This protects
    /// the whole production helper surface without banning ambient-unit reads. It
    /// does not infer arbitrary reflection or SQL transaction-control dataflow.
    /// </summary>
    [Fact]
    public void Integration_delivery_lifecycle_stays_with_its_enumerated_owners()
    {
        using var census = ProductionCensus.Read();
        IntegrationLifecycleOffenders(census).Should().BeEmpty(
            "Fix: let the common owner fence and complete business delivery; retain only the named acceptance, persistence and standalone transaction owners");
    }

    [Fact]
    public void Integration_delivery_lifecycle_census_rejects_typed_helper_async_delegate_and_missing_paths()
    {
        using var census = ProductionCensus.Read();
        var types = census.Types;
        var unit = types[(KernelAssembly, LifecycleUnit)];
        var scope = types[(KernelAssembly, LifecycleScope)];
        var adapter = types[(InfrastructureAssembly, LifecycleAdapter)];
        var frame = types[(InfrastructureAssembly, LifecycleFrame)];
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var capabilities = new[] { unit, adapter }.SelectMany(type => type.Methods.Where(method =>
                method.Name is "BeginTransactionAsync" or "CommitAsync" or "RollbackAsync"))
            .Concat(new[] { scope, frame }.SelectMany(type => type.Methods.Where(method => method.Name is "CompleteAsync" or "FailAsync")))
            .Cast<MethodReference>().Concat(new[] { typeof(DbTransaction), typeof(IDbTransaction), typeof(NpgsqlTransaction) }
                .SelectMany(type => type.GetMethods().Where(method => LifecycleTerminalMethods.Contains(method.Name, StringComparer.Ordinal)
                    && (method.GetParameters().Length == 0 || method.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(CancellationToken))))
                .Select(method => application.ImportReference(method))).ToArray();
        foreach (var capability in capabilities)
        {
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "extra typed lifecycle authority through " + opcode.Name, () =>
                    AddCallProbe(application, "IntegrationLifecycleEscape", capability, opcode), IntegrationLifecycleOffenders);
            AssertMutation(census, "an asynchronous lifecycle helper", () =>
                AddAsyncCallProbe(application, "AsyncIntegrationLifecycleEscape", capability), IntegrationLifecycleOffenders);
        }
        foreach (var module in census.Modules)
            AssertMutation(census, "a lifecycle bypass in " + module.Name, () =>
                AddCallProbe(module, "IntegrationLifecycleLayerEscape", unit.Methods.Single(method => method.Name == "CommitAsync"), OpCodes.Callvirt), IntegrationLifecycleOffenders);

        foreach (var subject in new[] { adapter, frame })
        {
            var module = subject.Module;
            var declaring = subject.DeclaringType;
            AssertMutation(census, "a missing real lifecycle implementation", () =>
            {
                if (declaring is null) module.Types.Remove(subject);
                else declaring.NestedTypes.Remove(subject);
                return () =>
                {
                    if (declaring is null) module.Types.Add(subject);
                    else declaring.NestedTypes.Add(subject);
                };
            }, IntegrationLifecycleOffenders);
        }
        AssertMutation(census, "an extra unit implementation", () =>
        {
            var probe = AddProbe(application, "AdditionalIntegrationUnit");
            probe.Interfaces.Add(new InterfaceImplementation(application.ImportReference(unit)));
            return () => application.Types.Remove(probe);
        }, IntegrationLifecycleOffenders);
        var complete = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == DeliveryCoordinator
                && item.Owner.Name == "ExecuteFrameAsync").SelectMany(item => item.Body.Body.Instructions)
            .Single(instruction => instruction.Operand is MethodReference target && target.Name == "CompleteAsync"
                && SameType(target.DeclaringType, scope));
        AssertMutation(census, "a missing real owner completion site", () =>
            ReplaceInstruction(complete, OpCodes.Ldstr, "missing owner completion"), IntegrationLifecycleOffenders);
        var physicalCommit = OwnedBodies(census).Where(item => item.Owner.DeclaringType.FullName == LifecycleAdapter
                && item.Owner.Name == "CommitFrameAsync").SelectMany(item => item.Body.Body.Instructions)
            .Single(instruction => instruction.Operand is MethodReference target
                && IntegrationLifecycleCapability(target, types) == "database.CommitAsync");
        AssertMutation(census, "a missing real physical completion site", () =>
            ReplaceInstruction(physicalCommit, OpCodes.Ldstr, "missing physical completion"), IntegrationLifecycleOffenders);
        var clean = AddProbe(application, "CleanIntegrationLifecycleObserver");
        foreach (var method in unit.Methods.Where(method => method.IsGetter || method.Name == "MarkRollbackOnly")
                     .Concat(scope.Methods.Where(method => method.IsGetter)))
            AddCall(clean, "Observe" + method.Name, method, OpCodes.Callvirt);
        try { IntegrationLifecycleOffenders(census).Should().BeEmpty("ambient reads and poisoning cannot grant commit ownership"); }
        finally { application.Types.Remove(clean); }

        using var probes = ModuleDefinition.ReadModule(typeof(OutboxDispatchConventionTests).Assembly.Location);
        census.Modules.Add(probes);
        try
        {
            var sites = CallSites(census).Where(site => !site.Owner.StartsWith(probes.Assembly.Name.Name + ":", StringComparison.Ordinal)
                || site.Owner.Contains("/IntegrationLifecycleProbe", StringComparison.Ordinal));
            var offenders = IntegrationLifecycleCallsiteOffenders(census.Types, sites);
            foreach (var name in new[] { "Commit", "Rollback", "Begin", "Complete", "Fail", "Concrete", "RawCommit", "RawRollback",
                         "ConcreteRaw", "InheritedUnit", "InheritedRaw", "Async", "Capture", "Run" })
                offenders.Should().Contain(offender => offender.Contains("::" + name + "(", StringComparison.Ordinal),
                    "Fix: the real lifecycle predicate must reject compiled " + name);
            offenders.Should().NotContain(offender => offender.Contains("/IntegrationLifecycleProbeClean::", StringComparison.Ordinal),
                "ordinary unit observations and rollback-only marking remain available");
        }
        finally { census.Modules.Remove(probes); }
    }

    private static SortedSet<string> IntegrationLifecycleOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        CheckSet(offenders, "lifecycle production assemblies", census.Modules.Select(module => module.Assembly.Name.Name), ProductionAssemblies.Names());
        var types = census.Types;
        foreach (var subject in new[] { (KernelAssembly, LifecycleUnit), (KernelAssembly, LifecycleScope),
                     (InfrastructureAssembly, LifecycleAdapter), (InfrastructureAssembly, LifecycleFrame) })
            if (!types.ContainsKey(subject)) offenders.Add("Missing lifecycle subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var unit = types[(KernelAssembly, LifecycleUnit)];
        var scope = types[(KernelAssembly, LifecycleScope)];
        var adapter = types[(InfrastructureAssembly, LifecycleAdapter)];
        var frame = types[(InfrastructureAssembly, LifecycleFrame)];
        CheckSet(offenders, "unit lifecycle implementations", types.Values.Where(type => !type.IsInterface && Derives(type, unit, types)).Select(TypeId), [TypeId(adapter)]);
        CheckSet(offenders, "frame lifecycle implementations", types.Values.Where(type => !type.IsInterface && Derives(type, scope, types)).Select(TypeId), [TypeId(frame)]);
        if (!adapter.IsSealed || !frame.IsSealed || !frame.IsNestedPrivate) offenders.Add("The unit adapter and its private frame must remain sealed");
        foreach (var type in new[] { unit, adapter })
        {
            CheckLifecycleDeclarations(type, "BeginTransactionAsync", LifecycleScopeTask,
                [[BoundaryCancellation], ["LearnStack.SharedKernel.Persistence.TransactionMode", BoundaryCancellation]]);
            CheckLifecycleDeclarations(type, "CommitAsync", LifecycleTask, [[BoundaryCancellation]]);
            CheckLifecycleDeclarations(type, "RollbackAsync", LifecycleTask, [[BoundaryCancellation]]);
        }
        foreach (var type in new[] { scope, frame })
        {
            CheckLifecycleDeclarations(type, "CompleteAsync", LifecycleTask, [[BoundaryCancellation]]);
            CheckLifecycleDeclarations(type, "FailAsync", LifecycleTask, [[BoundaryCancellation]]);
        }
        offenders.UnionWith(IntegrationLifecycleCallsiteOffenders(types, CallSites(census)));
        return offenders;

        void CheckLifecycleDeclarations(TypeDefinition type, string name, string returnType, string[][] signatures)
        {
            var declarations = type.Methods.Where(method => method.Name == name).ToArray();
            if (declarations.Length != signatures.Length || declarations.Any(method => method.IsStatic || !method.IsPublic || method.ReturnType.FullName != returnType)
                || signatures.Any(signature => declarations.Count(method => ParametersMatch(method, signature)) != 1))
                offenders.Add("Unexpected lifecycle declaration: " + TypeId(type) + "." + name);
        }
    }

    private static SortedSet<string> IntegrationLifecycleCallsiteOffenders(
        Dictionary<(string Assembly, string Type), TypeDefinition> types,
        IEnumerable<(string Owner, MethodReference Target)> sites)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var expected = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["unit.begin"] = [Owner(InfrastructureAssembly, LifecycleAcceptance, "AcceptAsync")],
            ["unit.begin-mode"] = [Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteAsync"),
                Owner(DeliveryApplication, DeliveryPrepared, "CreateAsync"), Owner(InfrastructureAssembly, LifecycleAdapter, "BeginTransactionAsync", [BoundaryCancellation])],
            ["unit.commit"] = [Owner(InfrastructureAssembly, LifecycleFrame, "CompleteAsync")],
            ["unit.rollback"] = [Owner(InfrastructureAssembly, LifecycleFrame, "FailAsync")],
            ["scope.complete"] = [Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"), Owner(InfrastructureAssembly, LifecycleAcceptance, "AcceptAsync")],
            ["scope.fail"] = [Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"), Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"),
                Owner(DeliveryApplication, DeliveryPrepared, "CreateAsync"), Owner(InfrastructureAssembly, LifecycleAcceptance, "AcceptAsync"), Owner(InfrastructureAssembly, LifecycleFrame, "DisposeAsync")],
            ["database.CommitAsync"] =
            [
                Owner(InfrastructureAssembly, LifecycleAdapter, "CommitFrameAsync"),
                Owner(InfrastructureAssembly, Store, "AcquireAsync"), Owner(InfrastructureAssembly, Store, "TryTerminalizeAsync"),
                Owner(InfrastructureAssembly, DeliveryStore, "AcquireAsync"), Owner(InfrastructureAssembly, DeliveryStore, "UpdateAsync"),
                Owner(InfrastructureAssembly, DeliveryStore, "TryTerminalizeAsync"), Owner(InfrastructureAssembly, DeliveryStore, "ReplayAsync"),
                Owner(AuditAssembly, AuditStore, "WriteOwnTransactionAsync"),
                Owner(AuditAssembly, "LearnStack.Infrastructure.Audit.AuditConfigService", "LoadAsync"),
                Owner(InfrastructureAssembly, LifecyclePlatform, "RecordAsync"), Owner(InfrastructureAssembly, LifecyclePlatform + "/Handle", "CommitAsync"),
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.MultiTenancy.CachedHostToTenantResolver", "ReadAsync"),
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.MultiTenancy.OrganizationScopeValidator", "BelongsToTenantAsync"),
                Owner("LearnStack.Modules.Tenancy.Infrastructure", "LearnStack.Modules.Tenancy.Infrastructure.FeatureFlags", "LoadTenantFlagsAsync"),
            ],
            ["database.RollbackAsync"] = [Owner(InfrastructureAssembly, LifecycleAdapter, "RollbackCoreAsync"), Owner(InfrastructureAssembly, LifecyclePlatform + "/Handle", "DisposeAsync")],
            ["database.Commit"] = [],
            ["database.Rollback"] = [],
        };
        var actual = sites.Select(site => (site.Owner, site.Target, Capability: IntegrationLifecycleCapability(site.Target, types)))
            .Where(site => site.Capability is not null).ToArray();
        foreach (var (capability, owners) in expected)
            CheckSet(offenders, "typed lifecycle " + capability, actual.Where(site => site.Capability == capability).Select(site => site.Owner), owners);
        foreach (var site in actual.Where(site => !expected.ContainsKey(site.Capability!)))
            offenders.Add("Unexpected lifecycle signature: " + site.Owner + " -> " + site.Target.FullName);
        return offenders;

        string Owner(string assembly, string typeName, string name, string[]? parameters = null)
        {
            if (!types.TryGetValue((assembly, typeName), out var type))
            {
                offenders.Add("Missing actual lifecycle owner: " + typeName);
                return "missing:" + typeName + "." + name;
            }
            var methods = type.Methods.Where(method => method.Name == name && (parameters is null || ParametersMatch(method, parameters))).ToArray();
            if (methods.Length == 1) return MethodId(methods[0]);
            offenders.Add("Missing or ambiguous lifecycle owner: " + typeName + "." + name);
            return "missing:" + typeName + "." + name;
        }
    }

    private static string? IntegrationLifecycleCapability(MethodReference target,
        Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        var name = target.Name[(target.Name.LastIndexOf('.') + 1)..];
        var cancellation = ParametersMatch(target, [BoundaryCancellation]);
        if (types.TryGetValue((KernelAssembly, LifecycleUnit), out var unit)
            && (SameType(target.DeclaringType, unit) || Derives(target.DeclaringType, unit, types)))
            return name switch
            {
                "BeginTransactionAsync" when cancellation && target.ReturnType.FullName == LifecycleScopeTask => "unit.begin",
                "BeginTransactionAsync" when ParametersMatch(target, ["LearnStack.SharedKernel.Persistence.TransactionMode", BoundaryCancellation])
                    && target.ReturnType.FullName == LifecycleScopeTask => "unit.begin-mode",
                "CommitAsync" when cancellation && target.ReturnType.FullName == LifecycleTask => "unit.commit",
                "RollbackAsync" when cancellation && target.ReturnType.FullName == LifecycleTask => "unit.rollback",
                "BeginTransactionAsync" or "CommitAsync" or "RollbackAsync" => "invalid-unit",
                _ => null,
            };
        if (types.TryGetValue((KernelAssembly, LifecycleScope), out var scope)
            && (SameType(target.DeclaringType, scope) || Derives(target.DeclaringType, scope, types)))
            return name switch
            {
                "CompleteAsync" when cancellation && target.ReturnType.FullName == LifecycleTask => "scope.complete",
                "FailAsync" when cancellation && target.ReturnType.FullName == LifecycleTask => "scope.fail",
                "CompleteAsync" or "FailAsync" => "invalid-scope",
                _ => null,
            };
        if (!LifecycleTerminalMethods.Contains(name, StringComparer.Ordinal) || !IsIntegrationDatabaseTransaction(target.DeclaringType, types)) return null;
        if ((name is "CommitAsync" or "RollbackAsync") && cancellation && target.ReturnType.FullName == LifecycleTask
            || (name is "Commit" or "Rollback") && target.Parameters.Count == 0 && target.ReturnType.FullName == "System.Void")
            return "database." + name;
        return "invalid-database";
    }

    private static bool IsIntegrationDatabaseTransaction(TypeReference type,
        Dictionary<(string Assembly, string Type), TypeDefinition> types)
    {
        var pending = new Queue<TypeReference>();
        var visited = new HashSet<(string Assembly, string Type)>();
        pending.Enqueue(type);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(Key(current))) continue;
            if (Key(current) is ("System.Data.Common", "System.Data.Common.DbTransaction" or "System.Data.IDbTransaction")
                or ("Npgsql", "Npgsql.NpgsqlTransaction")) return true;
            if (!types.TryGetValue(Key(current), out var definition)) continue;
            if (definition.BaseType is { } parent) pending.Enqueue(parent);
            foreach (var contract in definition.Interfaces) pending.Enqueue(contract.InterfaceType);
        }
        return false;
    }

    private interface IIntegrationLifecycleProbeUnit : IUnitOfWork
    {
        new Task CommitAsync(CancellationToken cancellationToken = default);
    }
    private abstract class IntegrationLifecycleProbeTransaction : DbTransaction
    {
        public override Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private interface IIntegrationLifecycleProbeHelper { Task Run(IUnitOfWork unit); }
    private sealed class IntegrationLifecycleProbeHelper : IIntegrationLifecycleProbeHelper
    {
        public Task Run(IUnitOfWork unit) => unit.CommitAsync();
    }
    private static class IntegrationLifecycleProbeCalls
    {
        public static Task Commit(IUnitOfWork unit) => unit.CommitAsync();
        public static Task Rollback(IUnitOfWork unit) => unit.RollbackAsync();
        public static Task<IUnitOfWorkScope> Begin(IUnitOfWork unit) => unit.BeginTransactionAsync();
        public static Task Complete(IUnitOfWorkScope frame) => frame.CompleteAsync();
        public static Task Fail(IUnitOfWorkScope frame) => frame.FailAsync();
        public static Task Concrete(NpgsqlUnitOfWork unit) => unit.CommitAsync();
        public static Task RawCommit(DbTransaction transaction) => transaction.CommitAsync();
        public static void RawRollback(IDbTransaction transaction) => transaction.Rollback();
        public static Task ConcreteRaw(NpgsqlTransaction transaction) => transaction.CommitAsync();
        public static Task InheritedUnit(IIntegrationLifecycleProbeUnit unit) => unit.CommitAsync();
        public static Task InheritedRaw(IntegrationLifecycleProbeTransaction transaction) => transaction.CommitAsync();
        public static Task ViaHelper(IIntegrationLifecycleProbeHelper helper, IUnitOfWork unit) => helper.Run(unit);
        public static async Task Async(IUnitOfWork unit) { await Task.Yield(); await unit.CommitAsync(); }
        public static Func<CancellationToken, Task> Capture(IUnitOfWork unit) => unit.CommitAsync;
    }
    private static class IntegrationLifecycleProbeClean
    {
        public static DbTransaction? Read(IUnitOfWork unit) => unit.Transaction;
        public static bool ReadFrame(IUnitOfWorkScope frame) => frame.IsOwner;
        public static void Refuse(IUnitOfWork unit) => unit.MarkRollbackOnly();
    }
}
