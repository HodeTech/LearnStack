using FluentAssertions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string JobAdmission = "LearnStack.Application.Jobs.BackgroundJobExecutionAdmission";
    private const string JobTenantBehavior = "LearnStack.Application.Pipeline.TenantContextBehavior`2";
    private const string JobTransactionBehavior = "LearnStack.Application.Pipeline.TransactionBehavior`2";
    private const string PrivilegedSource = "LearnStack.Infrastructure.Persistence.PrivilegedDataSource";

    /// <summary>ADR-0047 ordinary execution authority; Step 1 prepares the closed activation seam.</summary>
    [Fact]
    public void Background_job_admission_and_privileged_entries_keep_their_closed_preconstruction_seams()
    {
        using var census = ProductionCensus.Read();
        JobAuthorityOffenders(census).Should().BeEmpty(
            "Fix: retain the independent sticky admission, exact pipeline gate and guards at every alternate/privileged entry");
    }

    /// <summary>ADR-0047: prove the same production predicate detects removal and additional activation.</summary>
    [Fact]
    public void Background_job_authority_census_detects_extra_activation_and_removed_live_guards()
    {
        using var census = ProductionCensus.Read();
        var admission = census.Types[(DeliveryApplication, JobAdmission)];
        var enter = admission.Methods.Single(method => method.Name == "Enter");
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
            AssertMutation(census, "an unauthorized admission activator through " + opcode, () =>
                AddCallProbe(application, "AdditionalJobActivator", enter, opcode), JobAuthorityOffenders);
        AssertMutation(census, "an async admission activator", () =>
            AddAsyncCallProbe(application, "AsyncJobActivator", enter), JobAuthorityOffenders);
        var guards = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference target && target.DeclaringType.FullName == JobAdmission
                && target.Name is "TryEnterRequest" or "RefusePrivilegedEntry" or "RequireContext" or "ModeFor").ToArray();
        guards.Should().NotBeEmpty("the real runtime gates must be nonempty subjects before dirty controls run");
        foreach (var guard in guards)
            AssertMutation(census, "a missing actual job authority gate", () =>
                ReplaceInstruction(guard, OpCodes.Ldstr, "missing job authority gate"), JobAuthorityOffenders);
    }

    private static SortedSet<string> JobAuthorityOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var types = census.Types;
        foreach (var subject in new[] { (DeliveryApplication, JobAdmission), (InfrastructureAssembly, PrivilegedSource),
                     (DeliveryApplication, JobTenantBehavior), (DeliveryApplication, JobTransactionBehavior) })
            if (!types.ContainsKey(subject)) offenders.Add("Missing job authority subject: " + subject.Item2);
        if (offenders.Count != 0) return offenders;
        var admission = types[(DeliveryApplication, JobAdmission)];
        var enter = admission.Methods.Where(method => method.Name == "Enter").ToArray();
        if (enter.Length != 1 || !ParametersMatch(enter[0], ["System.String", "LearnStack.SharedKernel.Identifiers.TenantId",
                "System.Nullable`1<LearnStack.SharedKernel.Identifiers.OrganizationId>", "LearnStack.SharedKernel.Jobs.BackgroundJobExecutionStage"]))
            offenders.Add("Admission activation must use its one registered definition/scope/stage contract");
        var snapshot = admission.NestedTypes.SingleOrDefault(type => type.Name == "Admission");
        if (snapshot is null || !snapshot.IsSealed || snapshot.Fields.Any(field => !field.IsInitOnly)
            || snapshot.Fields.Any(field => field.FieldType.FullName is "LearnStack.SharedKernel.Jobs.IBackgroundJobRequestCatalog"
                or "LearnStack.SharedKernel.Audit.IAuditCatalog"))
            offenders.Add("Admission must retain a sealed immutable snapshot without mutable catalogue authority");
        var sites = CallSites(census).Where(site => site.Target.DeclaringType.FullName == JobAdmission).ToArray();
        // Step 1 prepares the activation seam. JobExecutor is not implemented yet;
        // no synthetic production caller is added to make this empty set look live.
        CheckSet(offenders, "ordinary job admission activators", sites.Where(site => site.Target.Name == "Enter").Select(site => site.Owner), []);
        CheckSet(offenders, "job request gate", sites.Where(site => site.Target.Name == "TryEnterRequest").Select(site => site.Owner),
            [Owner(DeliveryApplication, JobTenantBehavior, "Handle")]);
        CheckSet(offenders, "closed framework transaction mode", sites.Where(site => site.Target.Name == "ModeFor").Select(site => site.Owner),
            [Owner(DeliveryApplication, JobTransactionBehavior, "Handle")]);
        CheckSet(offenders, "job context owner gates", sites.Where(site => site.Target.Name == "RequireContext").Select(site => site.Owner),
            [Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteAsync"), Owner(DeliveryApplication, DeliveryCoordinator, "ExecuteFrameAsync")]);
        CheckSet(offenders, "privileged/alternate entry guards", sites.Where(site => site.Target.Name == "RefusePrivilegedEntry").Select(site => site.Owner),
            [Owner(InfrastructureAssembly, PrivilegedSource, "OpenConnection"), Owner(InfrastructureAssembly, PrivilegedSource, "OpenConnectionAsync"),
             Owner(InfrastructureAssembly, BoundaryPlatform, "EnterAsync"), Owner(InfrastructureAssembly, BoundaryTransport, "PublishAsync"),
             Owner(InfrastructureAssembly, DeliveryExecutorType, "ExecuteAsync"), Owner(InfrastructureAssembly, DeliveryExecutorType, "ExecuteControlledAsync"),
             Owner(DeliveryApplication, DeliveryCoordinator, "PrepareDeliveryOwnerAsync"), Owner(DeliveryApplication, DeliveryCoordinator, "ExecutePreparedDeliveryAsync")]);
        return offenders;

        string Owner(string assembly, string typeName, string name)
        {
            if (!types.TryGetValue((assembly, typeName), out var type))
            { offenders.Add("Missing guarded entry: " + typeName); return "missing:" + typeName; }
            var methods = type.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1) { offenders.Add("Guarded entry must have one exact method: " + typeName + "." + name); return "missing:" + name; }
            return MethodId(methods[0]);
        }
    }
}
