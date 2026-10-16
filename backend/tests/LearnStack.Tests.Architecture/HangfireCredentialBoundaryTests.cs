using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string VendorFactory = "LearnStack.Infrastructure.Jobs.HangfireConnectionFactory";
    private const string VendorBootstrap = "LearnStack.Infrastructure.Jobs.HangfireStorageBootstrap";
    private const string JobBridgeStore = "LearnStack.Infrastructure.Jobs.PostgresBackgroundJobBridgeStore";

    [Fact]
    public void Vendor_connection_capability_rejects_extra_acquisition_forwarding_and_factory_construction()
    {
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, CredentialOffenders).Should().BeEmpty("the actual vendor factory and bootstrap must be clean subjects");
        var source = census.Types[(InfrastructureAssembly, BoundaryGuardedSource)];
        var acquire = source.Methods.Single(method => method.Name == "OpenConnection");
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
            AssertMutation(census, "an extra guarded-source acquisition through " + opcode, () =>
                AddCallProbe(application, "ExtraVendorAcquisition", acquire, opcode), CredentialOffenders);
        AssertMutation(census, "an extra async guarded-source acquisition", () =>
            AddAsyncCallProbe(application, "ExtraAsyncVendorAcquisition", acquire), CredentialOffenders);

        var factory = census.Types[(InfrastructureAssembly, VendorFactory)];
        AssertMutation(census, "a second production factory constructor caller", () =>
            AddCallProbe(application, "ExtraVendorFactory", factory.Methods.Single(method => method.IsConstructor), OpCodes.Newobj),
            CredentialOffenders);
        AssertMutation(census, "a source-forwarding overload borrowing the factory entry name", () =>
            AddBoundaryFieldGetter(factory, "GetOrCreateConnection", "_source"), CredentialOffenders);
        AssertMutation(census, "a second acquisition inside the exact approved factory", () =>
        {
            var method = factory.Methods.Single(item => item.Name == "GetOrCreateConnection");
            var instruction = Instruction.Create(OpCodes.Callvirt, method.Module.ImportReference(acquire));
            method.Body.Instructions.Add(instruction);
            return () => method.Body.Instructions.Remove(instruction);
        }, CredentialOffenders);
        var bootstrap = census.Types[(InfrastructureAssembly, VendorBootstrap)];
        AssertMutation(census, "an extra bootstrap acquisition overload", () =>
        {
            var helper = AddCall(bootstrap, "Create", acquire, OpCodes.Callvirt);
            return () => bootstrap.Methods.Remove(helper);
        }, CredentialOffenders);
    }

    private static void CheckPrivilegedAcquisitions(SortedSet<string> offenders, ProductionCensus census)
    {
        var expected = new List<string> { CheckJobBridgeCredentials(offenders, census) };
        // These are capability acquisition sites, not every method of a permitted
        // type. Delegates and real compiler-linked async bodies share their owner's
        // identity; an overload or unrelated helper never grants itself authority.
        var sites = new[]
        {
            (BoundaryApi, BoundaryWorker, new[] { "StartAsync" }),
            (BoundaryApi, DeliveryWorker, new[] { "StartAsync" }),
            (InfrastructureAssembly, Store, new[] { "OpenConnectionAsync" }),
            (InfrastructureAssembly, DeliveryStore, new[] { "OpenConnectionAsync" }),
            (InfrastructureAssembly, JobRoutingStore, new[] { "TryClaimOwnedAsync" }),
            (InfrastructureAssembly, BoundaryPlatform, new[] { "EnterAsync" }),
            (InfrastructureAssembly, VendorFactory, new[] { "GetOrCreateConnection" }),
            (InfrastructureAssembly, VendorBootstrap, new[] { "Create" }),
        };
        foreach (var (assembly, name, methods) in sites)
        {
            if (!census.Types.TryGetValue((assembly, name), out var type))
            { offenders.Add("Missing privileged acquisition owner: " + name); continue; }
            foreach (var methodName in methods)
            {
                var declarations = type.Methods.Where(method => method.Name == methodName).ToArray();
                if (declarations.Length != 1) offenders.Add("Privileged acquisition requires one exact entry: " + name + "." + methodName);
                else expected.Add(MethodId(declarations[0]));
            }
        }
        CheckSet(offenders, "guarded physical connection acquisition callers", CallSites(census)
            .Where(site => site.Target.DeclaringType.FullName == BoundaryGuardedSource
                && site.Target.Name is "OpenConnection" or "OpenConnectionAsync").Select(site => site.Owner), expected);

        if (!census.Types.TryGetValue((InfrastructureAssembly, VendorFactory), out var factory)
            || !census.Types.TryGetValue((InfrastructureAssembly, VendorBootstrap), out var bootstrap)) return;
        var constructor = RequireBoundaryMethod(offenders, factory, ".ctor", "System.Void", MethodAttributes.Public,
            BoundaryGuardedSource, "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate");
        var reader = RequireBoundaryMethod(offenders, factory, "GetOrCreateConnection", "Npgsql.NpgsqlConnection", MethodAttributes.Public);
        CheckSourceFieldAccess(offenders, census, factory, constructor, [reader]);
        if (factory.Interfaces.Count != 1 || factory.Interfaces[0].InterfaceType.FullName != "Hangfire.PostgreSql.IConnectionFactory")
            offenders.Add("Vendor factory must implement its exact stock connection-factory contract");
        var create = bootstrap.Methods.Where(method => method.Name == "Create").ToArray();
        if (create.Length != 1 || !create[0].IsPublic || !create[0].IsStatic || create[0].HasGenericParameters
            || create[0].ReturnType.FullName != "Hangfire.PostgreSql.PostgreSqlStorage"
            || !ParametersMatch(create[0], [BoundaryGuardedSource,
                "LearnStack.Infrastructure.Jobs.BackgroundJobInfrastructureRegistry", "LearnStack.Infrastructure.Jobs.HangfireBoundedLogProvider",
                "LearnStack.Infrastructure.Jobs.BackgroundJobRuntimeGate"]))
            offenders.Add("Vendor bootstrap requires its exact static storage-validation entry");
        CheckSet(offenders, "vendor connection factory construction callers", CallSites(census)
            .Where(site => site.Target.DeclaringType.FullName == VendorFactory && site.Target.Name == ".ctor")
            .Select(site => site.Owner), create.Length == 1 ? [MethodId(create[0])] : []);
    }
}
