using System.Data;
using System.Data.Common;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
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
    private static readonly string[] LifecycleFacadeTerminalMethods = ["CommitTransaction", "RollbackTransaction", "CommitTransactionAsync", "RollbackTransactionAsync"];

    /// <summary>
    /// ADR-0046's completion gate and ADR-0057's common owner: typed unit/frame and
    /// database transaction completion and tenant announcement stay with the enumerated owners. This protects
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
                method.Name is "BeginTransactionAsync" or "CommitAsync" or "RollbackAsync" or "SetTenantContextAsync"))
            .Concat(new[] { scope, frame }.SelectMany(type => type.Methods.Where(method => method.Name is "CompleteAsync" or "FailAsync")))
            .Cast<MethodReference>().Concat(new[] { typeof(DbTransaction), typeof(IDbTransaction), typeof(NpgsqlTransaction),
                    typeof(IDbContextTransaction), typeof(RelationalTransaction), typeof(DatabaseFacade) }
                .SelectMany(type => type.GetMethods().Where(method => LifecycleTerminalMethods.Contains(method.Name, StringComparer.Ordinal)
                    || LifecycleFacadeTerminalMethods.Contains(method.Name, StringComparer.Ordinal))
                    .Where(method =>
                        method.GetParameters().Length == 0 || method.GetParameters() is [{ ParameterType: var parameter }] && parameter == typeof(CancellationToken)))
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
        foreach (var (assembly, type, name, parameters, terminalCapability) in new[]
                 {
                     (AuditAssembly, AuditStore, "WriteOwnTransactionCoreAsync",
                         new[] { "LearnStack.SharedKernel.Audit.AuditEntryDraft", "LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources", BoundaryCancellation }, "database.CommitAsync"),
                     (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "CommitAsync",
                         new[] { "LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", BoundaryCancellation }, "database.CommitAsync"),
                     (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobTerminalStore", "CommitAsync",
                         new[] { "LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", BoundaryCancellation }, "database.CommitAsync"),
                     (InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "ProbeCommitAsync",
                         new[] { "LearnStack.Infrastructure.Jobs.BackgroundJobCommitProbe", BoundaryCancellation }, "database.RollbackAsync"),
                 })
        {
            var owner = types[(assembly, type)].Methods.Single(method => method.Name == name && ParametersMatch(method, parameters));
            var terminals = OwnedBodies(census).Where(item => item.Owner == owner)
                .SelectMany(item => item.Body.Body.Instructions).Where(instruction => instruction.Operand is MethodReference target
                    && IntegrationLifecycleCapability(target, types) == terminalCapability).ToArray();
            terminals.Should().ContainSingle("the exact job helper owner must execute its real physical terminal operation");
            AssertMutation(census, "a missing actual job helper terminal operation in " + MethodId(owner), () =>
                ReplaceInstruction(terminals[0], OpCodes.Ldstr, "missing job helper terminal operation"), IntegrationLifecycleOffenders);
        }
        var announcements = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference target
                && IntegrationLifecycleCapability(target, types) == "unit.announce").ToArray();
        announcements.Should().HaveCount(3, "Fix: retain the ordinary owner and both durable transport announcement sites");
        foreach (var announcement in announcements)
            AssertMutation(census, "a missing real tenant announcement site", () =>
                ReplaceInstruction(announcement, OpCodes.Ldstr, "missing tenant announcement"), IntegrationLifecycleOffenders);
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
                         "ConcreteRaw", "InheritedUnit", "InheritedRaw", "Async", "Capture", "Run",
                         "Announce", "ConcreteAnnounce", "InheritedAnnounce", "AsyncAnnounce", "CaptureAnnouncement",
                         "EfCommit", "EfRollback", "EfCommitAsync", "EfRollbackAsync", "EfConcreteCommitAsync", "EfCurrentCommitAsync",
                         "EfFacadeCommit", "EfFacadeRollback", "EfFacadeCommitAsync", "EfFacadeRollbackAsync", "EfAsync", "EfCapture" })
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
            CheckLifecycleDeclarations(type, "SetTenantContextAsync", LifecycleTask,
                [["LearnStack.SharedKernel.Tenancy.ITenantContext", BoundaryCancellation]]);
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
            ["unit.begin-mode"] = [Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteCoreAsync"),
                Owner(DeliveryApplication, DeliveryPrepared, "CreateAsync"), Owner(InfrastructureAssembly, LifecycleAdapter, "BeginTransactionAsync", [BoundaryCancellation])],
            ["unit.commit"] = [Owner(InfrastructureAssembly, LifecycleFrame, "CompleteAsync")],
            ["unit.rollback"] = [Owner(InfrastructureAssembly, LifecycleFrame, "FailAsync")],
            ["unit.announce"] = [Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"),
                Owner(DeliveryApplication, DeliveryPrepared, "CreateAsync"), Owner(InfrastructureAssembly, LifecycleAcceptance, "AcceptAsync")],
            ["scope.complete"] = [Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"), Owner(InfrastructureAssembly, LifecycleAcceptance, "AcceptAsync")],
            ["scope.fail"] = [Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"), Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync"),
                Owner(DeliveryApplication, DeliveryPrepared, "CreateAsync"), Owner(InfrastructureAssembly, LifecycleAcceptance, "AcceptAsync"), Owner(InfrastructureAssembly, LifecycleFrame, "DisposeAsync")],
            ["database.CommitAsync"] =
            [
                Owner(InfrastructureAssembly, LifecycleAdapter, "CommitFrameAsync"),
                Owner(InfrastructureAssembly, Store, "AcquireAsync"), Owner(InfrastructureAssembly, Store, "TryTerminalizeAsync"),
                Owner(InfrastructureAssembly, DeliveryStore, "AcquireAsync"), Owner(InfrastructureAssembly, DeliveryStore, "UpdateAsync"),
                Owner(InfrastructureAssembly, DeliveryStore, "TryTerminalizeAsync"), Owner(InfrastructureAssembly, DeliveryStore, "ReplayAsync"),
                // Routing publishes a claim only after this owned operation acknowledges COMMIT and joins cleanup.
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobRoutingStore", "TryClaimOwnedAsync"),
                // Bridge transport hints own short privileged transactions, never business completion.
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobBridgeStore", "TryClaimAsync"),
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobBridgeStore", "UpdateAsync"),
                Owner(AuditAssembly, AuditStore, "WriteOwnTransactionCoreAsync",
                    ["LearnStack.SharedKernel.Audit.AuditEntryDraft", "LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources", BoundaryCancellation]),
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "CommitAsync",
                    ["LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", BoundaryCancellation]),
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobTerminalStore", "CommitAsync",
                    ["LearnStack.Infrastructure.Jobs.BackgroundJobControlResources", BoundaryCancellation]),
                Owner(AuditAssembly, "LearnStack.Infrastructure.Audit.AuditConfigService", "LoadAsync"),
                Owner(InfrastructureAssembly, LifecyclePlatform, "RecordAsync"), Owner(InfrastructureAssembly, LifecyclePlatform + "/Handle", "CommitAsync"),
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.MultiTenancy.CachedHostToTenantResolver", "ReadAsync"),
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.MultiTenancy.OrganizationScopeValidator", "BelongsToTenantAsync"),
                Owner("LearnStack.Modules.Tenancy.Infrastructure", "LearnStack.Modules.Tenancy.Infrastructure.FeatureFlags", "LoadTenantFlagsAsync"),
            ],
            ["database.RollbackAsync"] = [Owner(InfrastructureAssembly, LifecycleAdapter, "RollbackCoreAsync"),
                Owner(InfrastructureAssembly, LifecyclePlatform + "/Handle", "DisposeAsync"),
                Owner(InfrastructureAssembly, "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobControlStore", "ProbeCommitAsync",
                    ["LearnStack.Infrastructure.Jobs.BackgroundJobCommitProbe", BoundaryCancellation])],
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
                "SetTenantContextAsync" when ParametersMatch(target, ["LearnStack.SharedKernel.Tenancy.ITenantContext", BoundaryCancellation])
                    && target.ReturnType.FullName == LifecycleTask => "unit.announce",
                "BeginTransactionAsync" or "CommitAsync" or "RollbackAsync" or "SetTenantContextAsync" => "invalid-unit",
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
        if (LifecycleFacadeTerminalMethods.Contains(name, StringComparer.Ordinal)
            && IsIntegrationDatabaseType(target.DeclaringType, types,
                [("Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade")]))
            name = name.Replace("Transaction", string.Empty, StringComparison.Ordinal);
        else if (!LifecycleTerminalMethods.Contains(name, StringComparer.Ordinal) || !IsIntegrationDatabaseTransaction(target.DeclaringType, types)) return null;
        if ((name is "CommitAsync" or "RollbackAsync") && cancellation && target.ReturnType.FullName == LifecycleTask
            || (name is "Commit" or "Rollback") && target.Parameters.Count == 0 && target.ReturnType.FullName == "System.Void")
            return "database." + name;
        return "invalid-database";
    }

    private static bool IsIntegrationDatabaseTransaction(TypeReference type,
        Dictionary<(string Assembly, string Type), TypeDefinition> types) =>
        IsIntegrationDatabaseType(type, types,
        [
            ("System.Data.Common", "System.Data.Common.DbTransaction"),
            ("System.Data.Common", "System.Data.IDbTransaction"),
            ("Npgsql", "Npgsql.NpgsqlTransaction"),
            ("Microsoft.EntityFrameworkCore", "Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction"),
            ("Microsoft.EntityFrameworkCore.Relational", "Microsoft.EntityFrameworkCore.Storage.RelationalTransaction"),
        ]);

    private static bool IsIntegrationDatabaseType(TypeReference type,
        Dictionary<(string Assembly, string Type), TypeDefinition> types, (string Assembly, string Type)[] contracts)
    {
        var pending = new Queue<TypeReference>();
        var visited = new HashSet<(string Assembly, string Type)>();
        pending.Enqueue(type);
        while (pending.TryDequeue(out var current))
        {
            if (!visited.Add(Key(current))) continue;
            if (contracts.Contains(Key(current))) return true;
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
        public static Task Announce(IUnitOfWork unit, ITenantContext context) => unit.SetTenantContextAsync(context);
        public static Task ConcreteAnnounce(NpgsqlUnitOfWork unit, ITenantContext context) => unit.SetTenantContextAsync(context);
        public static Task InheritedAnnounce(IIntegrationLifecycleProbeUnit unit, ITenantContext context) => unit.SetTenantContextAsync(context);
        public static async Task AsyncAnnounce(IUnitOfWork unit, ITenantContext context) { await Task.Yield(); await unit.SetTenantContextAsync(context); }
        public static Func<ITenantContext, CancellationToken, Task> CaptureAnnouncement(IUnitOfWork unit) => unit.SetTenantContextAsync;
        public static void EfCommit(IDbContextTransaction transaction) => transaction.Commit();
        public static void EfRollback(IDbContextTransaction transaction) => transaction.Rollback();
        public static Task EfCommitAsync(IDbContextTransaction transaction) => transaction.CommitAsync();
        public static Task EfRollbackAsync(IDbContextTransaction transaction) => transaction.RollbackAsync();
        public static Task EfConcreteCommitAsync(RelationalTransaction transaction) => transaction.CommitAsync();
        public static Task EfCurrentCommitAsync(DbContext context) => context.Database.CurrentTransaction!.CommitAsync();
        public static void EfFacadeCommit(DatabaseFacade database) => database.CommitTransaction();
        public static void EfFacadeRollback(DatabaseFacade database) => database.RollbackTransaction();
        public static Task EfFacadeCommitAsync(DatabaseFacade database) => database.CommitTransactionAsync();
        public static Task EfFacadeRollbackAsync(DatabaseFacade database) => database.RollbackTransactionAsync();
        public static async Task EfAsync(IDbContextTransaction transaction) { await Task.Yield(); await transaction.CommitAsync(); }
        public static Func<CancellationToken, Task> EfCapture(IDbContextTransaction transaction) => transaction.CommitAsync;
    }
    private static class IntegrationLifecycleProbeClean
    {
        public static DbTransaction? Read(IUnitOfWork unit) => unit.Transaction;
        public static bool ReadFrame(IUnitOfWorkScope frame) => frame.IsOwner;
        public static void Refuse(IUnitOfWork unit) => unit.MarkRollbackOnly();
        public static IDbContextTransaction? ReadEfTransaction(DbContext context) => context.Database.CurrentTransaction;
        public static Guid ReadEfIdentity(IDbContextTransaction transaction) => transaction.TransactionId;
    }
}
