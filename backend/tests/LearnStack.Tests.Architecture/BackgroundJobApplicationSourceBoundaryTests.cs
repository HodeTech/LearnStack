using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class OutboxDispatchConventionTests
{
    private const string AppJobOwner = "LearnStack.SharedKernel.Jobs.JobApplicationSourceOwner";
    private const string AppJobResources = "LearnStack.SharedKernel.Jobs.JobApplicationAuxiliaryResources";
    private const string AppJobUnit = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
    private const string AppJobAuditConfig = "LearnStack.Infrastructure.Audit.AuditConfigService";
    private const string AppJobAuditStore = "LearnStack.Infrastructure.Audit.PostgresAuditStore";
    private const string AppJobFlags = "LearnStack.Modules.Tenancy.Infrastructure.FeatureFlags";
    private const string AppJobKillswitch = "LearnStack.Modules.Tenancy.Infrastructure.KillswitchOverlay";
    private const string AppJobRecovery = "LearnStack.Infrastructure.Jobs.BackgroundJobExecutionControl";
    private const string AppJobTerminalNoStart = "LearnStack.Infrastructure.Jobs.BackgroundJobTerminalNoStartOperation";
    private const string AppJobRecoveryReceipt = "LearnStack.Application.Jobs.BackgroundJobExternalRecoveryReceipt";
    private const string AppJobAuthorizationSource = "LearnStack.Infrastructure.Persistence.IdentityAuthorizationReadSource";
    private const string AppJobAuthorizationOwner = "LearnStack.Infrastructure.Persistence.AuthorizationReadOwner";

    private static SortedSet<string> JobApplicationSourceOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        var subjects = new[] { AppJobOwner, AppJobResources, AppJobUnit, AppJobAuditConfig, AppJobAuditStore,
            AppJobFlags, AppJobKillswitch, JobExecutorType,
            "LearnStack.Infrastructure.MultiTenancy.CachedHostToTenantResolver",
            "LearnStack.Infrastructure.MultiTenancy.OrganizationScopeValidator",
            AppJobRecovery, AppJobTerminalNoStart, AppJobRecoveryReceipt, JobAdmission,
            AppJobAuthorizationSource, AppJobAuthorizationOwner };
        var types = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var name in subjects)
        {
            var matches = census.Types.Values.Where(type => type.FullName == name).ToArray();
            if (matches.Length != 1) offenders.Add("Missing or ambiguous job application-source subject: " + name);
            else types.Add(name, matches[0]);
        }
        if (offenders.Count != 0) return offenders;
        var owner = types[AppJobOwner];
        var resources = types[AppJobResources];
        if (!owner.IsPublic || !owner.IsSealed || !resources.IsPublic || !resources.IsSealed)
            offenders.Add("The job application-source owner and concrete resources must be public sealed dependency seams");
        if (owner.Methods.Count(method => method.IsConstructor && !method.IsStatic) != 1
            || owner.Methods.Any(method => method.IsConstructor && !method.IsStatic && !method.IsPrivate)
            || resources.Methods.Count(method => method.IsConstructor && !method.IsStatic) != 1
            || resources.Methods.Any(method => method.IsConstructor && !method.IsStatic && !method.IsAssembly))
            offenders.Add("Job application-source constructors are closed");
        var ownerSurface = new[] { "Create", "get_Current", "get_IsCurrentBinding", "Enter", "get_CleanupConfirmed", "RequireBusinessReady",
            "RequireFreshScopeReady", "RequireOwnedMainUnit", "BindMainUnit", "HoldMainConnection", "HoldMainTransaction",
            "ReportMainTransactionCleanup", "ReportMainCleanup", "RefuseUnrelatedRead", "CloseIssuance", "JoinAuxiliaryCleanupAsync",
            "BeginAuditConfigLoad", "BeginStandaloneAudit", "BeginTenantFlagsLoad", "BeginKillswitchLoad",
            "BeginAuthorizationRead", "BeginAuthorizationEvidence",
            "get_IsExternal", "RefuseExternalEntry", "EnterExternal", "BindExternalRecovery" };
        var resourceSurface = new[] { "HoldConnection", "HoldTransaction", "HoldCommand", "HoldReader", "HoldActualTask",
            "DisposeReaderAsync", "CleanupAsync", "MarkCleanupFailed" };
        CheckSurface(owner, ownerSurface);
        CheckSurface(resources, resourceSurface);
        if (!owner.Methods.Any(method => method.Name == "JoinAuxiliaryCleanupAsync" && method.IsPublic
                && !method.IsStatic && method.Parameters.Count == 0
                && method.ReturnType.FullName == "System.Threading.Tasks.Task`1<System.Boolean>")
            || !resources.Methods.Any(method => method.Name == "JoinActualTaskAsync" && method.IsAssembly
                && !method.IsStatic && method.Parameters.Count == 0
                && method.ReturnType.FullName == "System.Threading.Tasks.Task"))
            offenders.Add("Auxiliary cleanup joins accept no token, callback or replacement task");
        var sites = CallSites(census).ToArray();
        var sourceInstructions = OwnedBodies(census).SelectMany(item => item.Body.Body.Instructions)
            .Where(instruction => instruction.Operand is MethodReference).ToArray();
        RecordDetachedTargets(offenders, sites);
        var sitesByTargetName = sites.ToLookup(site => site.Target.Name, StringComparer.Ordinal);
        var sourceInstructionsByTargetName = sourceInstructions.ToLookup(
            instruction => ((MethodReference)instruction.Operand).Name, StringComparer.Ordinal);

        // Original admission owns its source across sequential executor scopes.
        // Fresh recovery and terminal cleanup own distinct sources before their first app read.
        Exact(owner, "Create", [(JobExecutorType, "ExecutePublishedAsync"),
            (JobExecutorType, "ExecuteRecipientAsync"),
            (AppJobRecovery, "ExecuteExternalRecoveryAsync"), (AppJobTerminalNoStart, ".ctor")]);
        Exact(owner, ".ctor", [(AppJobOwner, "Create")]);
        Exact(owner, "Enter", [(JobExecutorType, "ExecutePublishedAsync"), (JobExecutorType, "ExecuteBusinessAsync"),
            (JobExecutorType, "ExecuteCapacityCleanupAsync"), (JobExecutorType, "ReadExternalStateCoreAsync"),
            (JobExecutorType, "WriteExternalCheckpointAsync"), (AppJobRecovery, "ExecuteExternalRecoveryAsync"),
            (JobExecutorType, "ExecuteRecipientAsync"),
            (AppJobTerminalNoStart, "ExecuteApplicationAsync")]);
        Exact(owner, "JoinAuxiliaryCleanupAsync", [(JobExecutorType, "ExecutePublishedAsync"), (JobExecutorType, "ExecuteBusinessAsync"),
            (JobExecutorType, "ExecuteCapacityCleanupAsync"), (JobExecutorType, "ReadExternalStateCoreAsync"),
            (JobExecutorType, "WriteExternalCheckpointAsync"), (AppJobTerminalNoStart, "ObserveActualAsync"),
            (JobExecutorType, "ExecuteRecipientAsync")]);
        Exact(owner, "BindExternalRecovery", [(AppJobRecoveryReceipt, "Bind")]);
        // This marker only denies app entry during provider construction/calls.
        Exact(owner, "EnterExternal", [(JobAdmission, "EnterExternal")]);
        Exact(owner, "get_IsCurrentBinding", [(JobAdmission, "IsOperationEvidenceCurrent")]);
        Exact(resources, "JoinActualTaskAsync", [(AppJobOwner, "JoinAuxiliaryCleanupAsync"),
            (AppJobOwner, "JoinAuxiliaryCleanupAsync"), (AppJobOwner, "JoinAuxiliaryCleanupAsync")]);
        Exact(owner, "BeginAuditConfigLoad", [(AppJobAuditConfig, "StartLoadAsync")]);
        Exact(owner, "BeginStandaloneAudit", [(AppJobAuditStore, "WriteOwnTransactionAsync")]);
        Exact(owner, "BeginTenantFlagsLoad", [(AppJobFlags, "StartTenantFlagsLoadAsync")]);
        Exact(owner, "BeginKillswitchLoad", [(AppJobKillswitch, "StartLoadAsync")]);
        Exact(owner, "BeginAuthorizationRead", [(AppJobAuthorizationSource, "OpenAsync")]);
        // Step 4 alone may activate this lane; its declaration supplies no production borrower today.
        Exact(owner, "BeginAuthorizationEvidence", []);
        Exact(owner, "BeginAuxiliary", [(AppJobOwner, "BeginAuditConfigLoad"), (AppJobOwner, "BeginStandaloneAudit"),
            (AppJobOwner, "BeginTenantFlagsLoad"), (AppJobOwner, "BeginKillswitchLoad")]);
        Exact(owner, "BeginConfinedLane", [(AppJobOwner, "BeginAuthorizationRead"), (AppJobOwner, "BeginAuthorizationEvidence")]);
        Exact(resources, ".ctor", [(AppJobOwner, "BeginAuxiliary"), (AppJobOwner, "BeginConfinedLane")]);
        var cores = new[] { (AppJobAuditConfig, "LoadAsync"), (AppJobAuditStore, "WriteOwnTransactionCoreAsync"),
            (AppJobFlags, "LoadTenantFlagsAsync"), (AppJobKillswitch, "LoadAsync") };
        Exact(resources, "HoldActualTask", [(AppJobAuditConfig, "StartLoadAsync"), (AppJobAuditStore, "WriteOwnTransactionAsync"),
            (AppJobFlags, "StartTenantFlagsLoadAsync"), (AppJobKillswitch, "StartLoadAsync"), (AppJobAuthorizationOwner, ".ctor")]);
        Exact(resources, "HoldConnection", [.. cores, (AppJobAuthorizationOwner, "OpenAsync")]);
        Exact(resources, "HoldTransaction", [.. cores.Take(3), (AppJobAuthorizationOwner, "OpenAsync")]);
        Exact(resources, "HoldCommand", [cores[0], cores[0], cores[0], cores[1], cores[1], cores[2], cores[2], cores[2], cores[3]]);
        Exact(resources, "HoldReader", [cores[0], cores[2], cores[3]]);
        Exact(resources, "DisposeReaderAsync", [cores[0], cores[2], (AppJobResources, "CleanupAsync")]);
        Exact(resources, "CleanupAsync", [.. cores, (AppJobAuthorizationOwner, "CleanupAsync")]);
        Exact(resources, "MarkCleanupFailed", [(AppJobAuthorizationOwner, "MarkCleanupFailed")]);
        Exact(owner, "ReportCleanupFailure", [(AppJobResources, "MarkCleanupFailed"), (AppJobResources, "DisposeReaderAsync"),
            (AppJobResources, "CleanupAsync")]);
        Exact(owner, "Release", [(AppJobResources, "TryRelease")]);
        Exact(resources, "MarkReleased", [(AppJobOwner, "Release")]);
        Exact(owner, "BindMainUnit", [(AppJobUnit, "BindJobMainUnit")]);
        Exact(owner, "HoldMainConnection", [(AppJobUnit, "get_Connection"), (AppJobUnit, "BeginTransactionCoreAsync")]);
        Exact(owner, "HoldMainTransaction", [(AppJobUnit, "BeginTransactionCoreAsync")]);
        Exact(owner, "ReportMainTransactionCleanup", [(AppJobUnit, "CommitFrameAsync"), (AppJobUnit, "CommitFrameAsync"),
            (AppJobUnit, "DisposeCompletedTransactionAsync"), (AppJobUnit, "DisposeCompletedTransactionAsync"),
            (AppJobUnit, "RollbackCoreAsync"), (AppJobUnit, "RollbackCoreAsync")]);
        Exact(owner, "ReportMainCleanup", [(AppJobUnit, "DisposeAsync"), (AppJobUnit, "DisposeAsync")]);
        Exact(owner, "RefuseUnrelatedRead", [(subjects[8], "ReadAsync"), (subjects[9], "BelongsToTenantAsync"),
            (AppJobAuthorizationSource, "QualifyAsync")]);
        CheckJobApplicationRetainedFields(census, owner, resources, offenders);
        return offenders;

        void CheckSurface(TypeDefinition subject, string[] expected)
        {
            var actual = subject.Methods.Where(method => method.IsPublic && !method.IsConstructor).ToArray();
            if (!actual.Select(method => method.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.Order(StringComparer.Ordinal)))
                offenders.Add("Unexpected job application-source public member or overload: " + subject.FullName);
            if (actual.SelectMany(method => method.Parameters).Any(parameter =>
                    parameter.ParameterType.FullName.StartsWith("System.Func", StringComparison.Ordinal)
                    || parameter.ParameterType.FullName.StartsWith("System.Action", StringComparison.Ordinal)
                    || parameter.ParameterType.FullName.StartsWith("Npgsql.", StringComparison.Ordinal)))
                offenders.Add("A job source seam accepts a provider or arbitrary callback");
        }

        void Exact(TypeDefinition subject, string name, (string Type, string Method)[] expected)
        {
            var methods = subject.Methods.Where(method => method.Name == name).ToArray();
            if (methods.Length != 1) { offenders.Add("Missing or overloaded job source seam: " + subject.FullName + "::" + name); return; }
            var allowed = new List<string>();
            foreach (var (typeName, methodName) in expected)
            {
                var callers = types[typeName].Methods.Where(method => method.Name == methodName
                    && (typeName != AppJobUnit || methodName != "BeginTransactionCoreAsync"
                        || method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(
                            ["LearnStack.SharedKernel.Persistence.TransactionMode", "System.Boolean", "System.Threading.CancellationToken"]))).ToArray();
                if (callers.Length != 1) { offenders.Add("Missing or overloaded job source caller: " + typeName + "::" + methodName); continue; }
                allowed.Add(MethodId(callers[0]));
            }
            var observed = sitesByTargetName[name].Where(site => TargetsDeclaredType(site.Target, subject) && site.Target.Name == name)
                .Select(site => site.Owner).Order(StringComparer.Ordinal).ToArray();
            if (!observed.SequenceEqual(allowed.Order(StringComparer.Ordinal)))
                offenders.Add("Unexpected job source caller census: " + subject.FullName + "::" + name);
            foreach (var instruction in sourceInstructionsByTargetName[name]
                         .Where(instruction => instruction.Operand is MethodReference target
                             && TargetsDeclaredType(target, subject) && target.Name == name))
                if (instruction.OpCode != OpCodes.Call && instruction.OpCode != OpCodes.Callvirt
                    && !(name == ".ctor" && instruction.OpCode == OpCodes.Newobj))
                    offenders.Add("Job source ownership cannot escape as a delegate: " + subject.FullName + "::" + name);
        }
    }

    public sealed partial class JobAuthorityMutationTests
    {

        [Fact]
        public void Background_job_application_source_ownership_has_exact_nonempty_callers_and_rejects_escapes()
        {
            using var census = ProductionCensus.Read();
            EvaluateDetector(census, JobApplicationSourceOffenders).Should().BeEmpty();
            var owner = census.Types.Values.Single(type => type.FullName == AppJobOwner);
            var resources = census.Types.Values.Single(type => type.FullName == AppJobResources);
            var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
            var guardedOwnerMethods = new[] { ".ctor", "Create", "Enter", "BeginAuditConfigLoad", "BeginStandaloneAudit", "BeginTenantFlagsLoad",
            "BeginKillswitchLoad", "BeginAuthorizationRead", "BeginAuthorizationEvidence", "BeginConfinedLane",
            "BindMainUnit", "HoldMainConnection", "HoldMainTransaction", "ReportMainTransactionCleanup",
            "ReportMainCleanup", "RefuseUnrelatedRead", "JoinAuxiliaryCleanupAsync", "BindExternalRecovery", "EnterExternal" };
            foreach (var target in owner.Methods.Where(method => guardedOwnerMethods.Contains(method.Name, StringComparer.Ordinal))
                         .Concat(resources.Methods.Where(method => method.IsPublic || method.IsConstructor
                             || method.Name == "JoinActualTaskAsync")).ToArray())
            {
                foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                    AssertMutation(census, "an off-owner job application-source capability through " + opcode,
                        () => AddCallProbe(application, "JobApplicationSourceEscape", target, opcode), JobApplicationSourceOffenders);
                AssertMutation(census, "an async job application-source escape",
                    () => AddAsyncCallProbe(application, "AsyncJobApplicationSourceEscape", target), JobApplicationSourceOffenders);
            }
            var begin = owner.Methods.Single(method => method.Name == "BeginAuditConfigLoad");
            AssertMutation(census, "a missing actual auxiliary seam", () =>
            {
                owner.Methods.Remove(begin);
                return () => owner.Methods.Add(begin);
            }, JobApplicationSourceOffenders);
            AssertMutation(census, "an added public acquisition overload", () =>
            {
                var extra = new MethodDefinition("BeginAuditConfigLoad", MethodAttributes.Public, begin.ReturnType);
                extra.Parameters.Add(new ParameterDefinition(owner.Module.TypeSystem.String));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                owner.Methods.Add(extra);
                return () => owner.Methods.Remove(extra);
            }, JobApplicationSourceOffenders);
            foreach (var join in new[] { owner.Methods.Single(method => method.Name == "JoinAuxiliaryCleanupAsync"),
                     resources.Methods.Single(method => method.Name == "JoinActualTaskAsync") })
            {
                var declaringType = join.DeclaringType;
                AssertMutation(census, "an auxiliary join accepting a caller cancellation token", () =>
                {
                    var token = new ParameterDefinition(join.Module.ImportReference(typeof(CancellationToken)));
                    join.Parameters.Add(token);
                    return () => join.Parameters.Remove(token);
                }, JobApplicationSourceOffenders);
                AssertMutation(census, "a missing actual auxiliary join", () =>
                {
                    declaringType.Methods.Remove(join);
                    return () => declaringType.Methods.Add(join);
                }, JobApplicationSourceOffenders);
            }
            // Literal expected boundaries are reviewed independently of the observed IL.
            // In particular, neither forwarding read overload is an allowed source binder/joiner.
            var lifecycleCallers = new (string Type, string Method, string[] Seams)[]
            {
            (JobExecutorType, "ExecutePublishedAsync", ["Create", "Enter", "JoinAuxiliaryCleanupAsync"]),
            (JobExecutorType, "ExecuteBusinessAsync", ["Enter", "JoinAuxiliaryCleanupAsync"]),
            (JobExecutorType, "ExecuteCapacityCleanupAsync", ["Enter", "JoinAuxiliaryCleanupAsync"]),
            (JobExecutorType, "ReadExternalStateCoreAsync", ["Enter", "JoinAuxiliaryCleanupAsync"]),
            (JobExecutorType, "WriteExternalCheckpointAsync", ["Enter", "JoinAuxiliaryCleanupAsync"]),
            (JobExecutorType, "ExecuteRecipientAsync", ["Create", "Enter", "JoinAuxiliaryCleanupAsync"]),
            (AppJobRecovery, "ExecuteExternalRecoveryAsync", ["Create", "Enter"]),
            (AppJobTerminalNoStart, ".ctor", ["Create"]),
            (AppJobTerminalNoStart, "ExecuteApplicationAsync", ["Enter"]),
            (AppJobTerminalNoStart, "ObserveActualAsync", ["JoinAuxiliaryCleanupAsync"]),
            (AppJobRecoveryReceipt, "Bind", ["BindExternalRecovery"]),
            (JobAdmission, "EnterExternal", ["EnterExternal"]),
            };
            foreach (var (typeName, methodName, expectedSeams) in lifecycleCallers)
            {
                var callerType = census.Types.Values.Single(type => type.FullName == typeName);
                var caller = callerType.Methods.Single(method => method.Name == methodName);
                var actualSites = OwnedBodies(census).Where(item => MethodId(item.Owner) == MethodId(caller))
                    .SelectMany(item => item.Body.Body.Instructions)
                    .Where(instruction => instruction.Operand is MethodReference target
                        && target.DeclaringType.FullName == AppJobOwner
                        && target.Name is "Create" or "Enter" or "JoinAuxiliaryCleanupAsync" or "BindExternalRecovery" or "EnterExternal")
                    .ToArray();
                actualSites.Select(site => ((MethodReference)site.Operand).Name).Should().BeEquivalentTo(expectedSeams,
                    "each approved caller retains its nonempty exact source boundary count");
                foreach (var site in actualSites)
                {
                    AssertMutation(census, "a deleted actual source boundary in " + typeName + "::" + methodName, () =>
                        ReplaceInstruction(site, OpCodes.Nop, null!), JobApplicationSourceOffenders);
                    foreach (var opcode in new[] { OpCodes.Ldftn, OpCodes.Ldvirtftn })
                        AssertMutation(census, "a delegated actual source boundary in " + typeName + "::" + methodName, () =>
                            ReplaceInstruction(site, opcode, site.Operand), JobApplicationSourceOffenders);
                }
                AssertMutation(census, "an unauthorized overload of a source caller " + typeName + "::" + methodName, () =>
                {
                    var extra = new MethodDefinition(caller.Name, caller.Attributes, caller.ReturnType);
                    foreach (var parameter in caller.Parameters)
                        extra.Parameters.Add(new ParameterDefinition(parameter.ParameterType));
                    extra.Parameters.Add(new ParameterDefinition(caller.Module.TypeSystem.String));
                    extra.Body.Instructions.Add(Instruction.Create(OpCodes.Call, (MethodReference)actualSites[0].Operand));
                    extra.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    callerType.Methods.Add(extra);
                    return () => callerType.Methods.Remove(extra);
                }, JobApplicationSourceOffenders);
            }
            // The real overloads share a name, but only the core owns the source.
            var executor = census.Types.Values.Single(type => type.FullName == JobExecutorType);
            var forwardingReads = executor.Methods.Where(method => method.Name == "ReadExternalStateAsync").ToArray();
            forwardingReads.Should().HaveCount(2);
            foreach (var forwardingRead in forwardingReads)
            {
                foreach (var seam in new[] { "Create", "Enter", "JoinAuxiliaryCleanupAsync" })
                {
                    var target = owner.Methods.Single(method => method.Name == seam);
                    foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                        AssertMutation(census, "a source boundary in a forwarding read overload " + MethodId(forwardingRead), () =>
                        {
                            var extra = Instruction.Create(opcode, target);
                            forwardingRead.Body.Instructions.Insert(0, extra);
                            return () => forwardingRead.Body.Instructions.Remove(extra);
                        }, JobApplicationSourceOffenders);
                }
            }
            AssertJobApplicationRetainedFieldMutations(census, owner, resources, application);
        }

    }

    private static SortedSet<string> JobApplicationFactoryOffenders(ProductionCensus census)
    {
        var offenders = new SortedSet<string>(StringComparer.Ordinal);
        const string compositionName = "LearnStack.Api.Composition.PersistenceCompositionExtensions";
        const string lifetimeName = "LearnStack.Infrastructure.Persistence.ApplicationDataSourceLifetime";
        var required = new[] { compositionName, lifetimeName, AppJobUnit, JobRuntime };
        var subjects = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
        foreach (var name in required)
        {
            var matches = census.Types.Values.Where(type => type.FullName == name).ToArray();
            if (matches.Length != 1) offenders.Add("Missing application-source factory subject: " + name);
            else subjects.Add(name, matches[0]);
        }
        if (offenders.Count != 0) return offenders;
        var composition = subjects[compositionName];
        var factory = composition.Methods.Where(method => method.Name == "CreateUnitOfWork").ToArray();
        var registration = composition.Methods.Where(method => method.Name == "AddLearnStackPersistence").ToArray();
        var unit = subjects[AppJobUnit].Methods.Where(method => method.Name == ".ctor").ToArray();
        var pin = subjects[lifetimeName].Methods.Where(method => method.Name == "RetainBackgroundJobsUntil").ToArray();
        var runtime = subjects[JobRuntime].Methods.Where(method => method.Name == ".ctor").ToArray();
        if (factory.Length != 1 || registration.Length != 1 || unit.Length != 1 || pin.Length != 1 || runtime.Length != 1)
        {
            offenders.Add("Application source factory, unit constructor and pin are nonempty exact seams");
            return offenders;
        }
        if (!factory[0].IsPrivate || !factory[0].IsStatic || factory[0].ReturnType.FullName != typeof(IUnitOfWork).FullName
            || !factory[0].Parameters.Select(parameter => parameter.ParameterType.FullName)
                .SequenceEqual([typeof(IServiceProvider).FullName!]))
            offenders.Add("The API unit factory must retain its exact private static signature");
        if (!pin[0].IsAssembly || pin[0].IsStatic || !subjects[lifetimeName].IsSealed)
            offenders.Add("Application source retention remains a closed runtime capability");
        var sites = CallSites(census).ToArray();
        Check(factory[0], [MethodId(registration[0])]);
        Check(unit[0], [MethodId(factory[0])]);
        Check(pin[0], [MethodId(runtime[0])]);
        var factoryCalls = sites.Where(site => site.Owner == MethodId(factory[0])).Select(site => site.Target).ToArray();
        if (factoryCalls.Count(target => target.Name == "GetRequiredService" && target is GenericInstanceMethod generic
                && generic.GenericArguments.SingleOrDefault()?.FullName == "System.Lazy`1<Npgsql.NpgsqlDataSource>") != 1)
            offenders.Add("The named unit factory must resolve the one owned Lazy application source");
        return offenders;

        void Check(MethodDefinition target, string[] expected)
        {
            var actual = sites.Where(site => TargetsDeclaredType(site.Target, target.DeclaringType)
                    && site.Target.Name == target.Name).Select(site => site.Owner).Order(StringComparer.Ordinal);
            if (!actual.SequenceEqual(expected.Order(StringComparer.Ordinal)))
                offenders.Add("Unexpected application source factory/pin caller: " + target.FullName);
        }
    }

    [Fact]
    public void The_API_application_source_has_one_owned_lazy_alias_named_factory_and_closed_actual_completion_pin()
    {
        var services = new ServiceCollection();
        services.AddLearnStackPersistence(new ConfigurationBuilder().Build());
        services.Should().NotContain(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource));
        services.Count(descriptor => descriptor.ServiceType == typeof(ApplicationDataSourceLifetime)).Should().Be(1);
        services.Count(descriptor => descriptor.ServiceType == typeof(Lazy<NpgsqlDataSource>)).Should().Be(1);
        var unit = services.Single(descriptor => descriptor.ServiceType == typeof(IUnitOfWork));
        unit.ImplementationType.Should().BeNull();
        unit.ImplementationFactory!.Method.Name.Should().Be("CreateUnitOfWork");
        unit.ImplementationFactory.Method.DeclaringType.Should().Be(typeof(PersistenceCompositionExtensions));
        using var census = ProductionCensus.Read();
        EvaluateDetector(census, JobApplicationFactoryOffenders).Should().BeEmpty();
        var application = census.Modules.Single(module => module.Assembly.Name.Name == DeliveryApplication);
        var factory = census.Types.Values.Single(type => type.FullName == typeof(PersistenceCompositionExtensions).FullName)
            .Methods.Single(method => method.Name == "CreateUnitOfWork");
        var pin = census.Types.Values.Single(type => type.FullName == typeof(ApplicationDataSourceLifetime).FullName)
            .Methods.Single(method => method.Name == "RetainBackgroundJobsUntil");
        foreach (var target in new[] { factory, pin })
        {
            foreach (var opcode in new[] { OpCodes.Call, OpCodes.Callvirt, OpCodes.Ldftn, OpCodes.Ldvirtftn })
                AssertMutation(census, "an extra application factory/retention capability through " + opcode,
                    () => AddCallProbe(application, "ApplicationLifetimeEscape", target, opcode), JobApplicationFactoryOffenders);
            AssertMutation(census, "an async application factory/retention escape",
                () => AddAsyncCallProbe(application, "AsyncApplicationLifetimeEscape", target), JobApplicationFactoryOffenders);
        }
    }
}
