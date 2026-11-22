using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Audit;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Audit;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class IdentityAuditConventionTests
{
    private const string BindingService = "LearnStack.Modules.Identity.Infrastructure.Bindings.GlobalIdentityBindingService";
    private const string InitialMigration = "LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations.create_identity_schema";
    private const string Installer = "LearnStack.Infrastructure.Audit.IdentityCreationWitnessInstaller";
    private const string RebindInstaller = "LearnStack.Infrastructure.Audit.IdentityRebindAuditInstaller";
    private const string RebindDdl = "LearnStack.Modules.Identity.Infrastructure.Persistence.IdentityAuthorityRebindDdl";
    private const string RebindRun = "LearnStack.Modules.Identity.Infrastructure.Bindings.IdentityIssuerRebind";
    private const string GlobalSource = "LearnStack.Application.Audit.AuditExecutionCoordinator+GlobalCreationSource";
    private const string InvitationJobStore = "LearnStack.Modules.Identity.Infrastructure.Invitations.InvitationEmailJobStore";
    private const string InvitationStager = "LearnStack.Modules.Identity.Infrastructure.Invitations.InvitationEmailStager";
    // ADR-0107 § 2: the data-request job store confirms its caller is one of the four export roots, as the invitation job store does.
    private const string DataRequestJobStore = "LearnStack.Modules.Identity.Infrastructure.DataRequests.DataRequestStore";
    private const string InvitationProducer = "LearnStack.Application.Messaging.ProducerExecution";
    private const string InvitationJobOrigin = InvitationProducer + "/JobOrigin";
    private const string InvitationRegistration = "LearnStack.Modules.Identity.Infrastructure.Invitations.InvitationEmailRegistration";
    private const string FrameworkRequests = "LearnStack.Application.Jobs.BackgroundJobFrameworkRequests";

    [Fact]
    public void Global_Identity_Creation_Uses_The_Common_Audit_Owner()
    {
        var types = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes()).ToArray();
        types.Where(type => type.IsSubclassOf(typeof(GlobalIdentityOwnerDescriptor.MintingSource)))
            .Select(type => type.FullName).Should().Equal(BindingService);
        types.Where(type => type.IsSubclassOf(typeof(GlobalIdentityCreationReference.MintingSource)))
            .Select(type => type.FullName).Should().Equal(GlobalSource);
        typeof(GlobalIdentityOwnerDescriptor).GetConstructors().Should().BeEmpty();
        typeof(GlobalIdentityCreationReference).GetConstructors().Should().BeEmpty();

        Callers(typeof(TransactionExecutionCoordinator).FullName!, "PrepareGlobalIdentityOwnerAsync")
            .Should().Equal(BindingService + ".CreateAsync");
        Callers(typeof(TransactionExecutionCoordinator.PreparedGlobalIdentityOwner).FullName!.Replace('+', '/'), "AdmitSerializedAbsence")
            .Should().Equal(BindingService + ".CreateAsync");
        Callers(typeof(TransactionExecutionCoordinator.PreparedGlobalIdentityOwner).FullName!.Replace('+', '/'), "CompleteExistingAsync")
            .Should().Equal(BindingService + ".CreateAsync");
        Callers(typeof(IAuditStore).FullName!, nameof(IAuditStore.WriteGlobalIdentityPendingAsync))
            .Should().Equal(typeof(TransactionExecutionCoordinator).FullName + ".ExecuteFrameAsync");
        Callers(typeof(IAuditStore).FullName!, nameof(IAuditStore.ReconcileGlobalIdentityAsync))
            .Should().Equal(typeof(AuditExecutionCoordinator).FullName + ".ReconcileAsync");
        // ADR-0104 § 8: the two witness-bound rebind writes have one production caller each, the rebind run itself.
        Callers(typeof(IAuditStore).FullName!, nameof(IAuditStore.WriteIdentityRebindIntentAsync))
            .Should().Equal(RebindRun + ".TryWriteIntentAsync");
        Callers(typeof(IAuditStore).FullName!, nameof(IAuditStore.WriteIdentityRebindResultAsync))
            .Should().Equal(RebindRun + ".RunCoreAsync");

        var audit = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "LearnStack.Infrastructure.Audit", "PostgresAuditStore.cs"));
        audit.Should().Contain("INSERT INTO audit_log");
        var installer = File.ReadAllText(Path.Combine(SourceScan.SourceRoot,
            "LearnStack.Infrastructure.Audit", "IdentityCreationWitnessInstaller.cs"));
        installer.Should().NotContain("INSERT INTO audit_log", "the installer defines witnesses, never an independent writer");
    }

    [Fact]
    public void Identity_infrastructure_core_edges_are_confined_to_exact_types_and_members()
    {
        using var module = ModuleDefinition.ReadModule(typeof(GlobalIdentityBindingService).Assembly.Location);
        var inspected = module.Types.Where(type => type.Name != "<Module>").ToArray();
        inspected.Should().NotBeEmpty();
        inspected.SelectMany(CoreEdgeViolations).Should().BeEmpty(
            "the Identity-only assembly exceptions do not admit any other common Application or Audit dependency");
        var migration = module.GetType(InitialMigration);
        migration.Should().NotBeNull();
        Il.MethodsNaming(migration!, Installer).Should().Equal("Down", "Up");
        Callers(Installer, "Install").Should().Equal(InitialMigration + ".Up");
        Callers(Installer, "Remove").Should().Equal(InitialMigration + ".Down");
        // ADR-0104: the rebind role's audit_log grant and policy arm have one central owner and one caller.
        Callers(RebindInstaller, "Install").Should().Equal(RebindDdl + ".Add");
        foreach (var owner in new[] { InvitationJobStore, InvitationStager, DataRequestJobStore })
        {
            var adapter = module.GetType(owner);
            var method = adapter.Methods.Single(candidate => candidate.Name == (owner == InvitationStager ? "StageAsync" : "RequireCurrentJob"));
            var extra = Instruction.Create(OpCodes.Ldftn, module.ImportReference(typeof(LearnStack.Application.Messaging.ProducerExecution).GetMethod("CaptureJobOrigin")!));
            method.Body.Instructions.Add(extra);
            try { CoreEdgeViolations(adapter).Should().NotBeEmpty("the admitted adapter cannot acquire another executable producer alias"); }
            finally { method.Body.Instructions.Remove(extra); }
        }
        var registration = module.GetType(InvitationRegistration);
        var registry = registration.Methods.Single(method => method.Name == "CreateRegistry");
        var frameworkRead = registry.Body.Instructions.Single(instruction => instruction.Operand is MethodReference target
            && target.DeclaringType.FullName == FrameworkRequests && target.Name == "get_All");
        var originalOpcode = frameworkRead.OpCode;
        foreach (var opcode in new[] { OpCodes.Nop, OpCodes.Ldftn })
        {
            frameworkRead.OpCode = opcode;
            try { CoreEdgeViolations(registration).Should().NotBeEmpty("the exact construction-free registry read is required and cannot escape as an alias"); }
            finally { frameworkRead.OpCode = originalOpcode; }
        }
        var extraFrameworkRead = Instruction.Create(OpCodes.Call, (MethodReference)frameworkRead.Operand);
        var definition = registration.Methods.Single(method => method.Name == "Definition");
        definition.Body.Instructions.Add(extraFrameworkRead);
        try { CoreEdgeViolations(registration).Should().NotBeEmpty("the declaration helper cannot acquire another framework registry read"); }
        finally { definition.Body.Instructions.Remove(extraFrameworkRead); }
    }

    [Fact]
    public void The_Operator_Token_Validator_Has_Two_Callers_Of_The_Profile_And_One_Of_Its_Own()
    {
        // ADR-0101 § 7: the witness comes from one validator with two callers. The API scheme reads a token through the
        // profile from its validated event; the offline validator reads one through the stock handler and then the
        // profile; and the offline validator is constructed and called by the rebind tool alone, so no endpoint, job or
        // module adapter can mint a witness by borrowing it.
        const string Profile = "LearnStack.Infrastructure.PlatformOperator.PlatformOperatorTokenProfile";
        const string Validator = "LearnStack.Infrastructure.PlatformOperator.PlatformOperatorTokenValidator";
        Callers(Profile, "TryRead").Should().Equal(
            "LearnStack.Api.Authentication.PlatformOperatorEvents.TokenValidated",
            Validator + ".ValidateAsync");
        Callers(Validator, "ValidateAsync").Should().Equal("LearnStack.Tools.IdentityAuthorityRebind.RebindTool.RunAdmittedAsync");
        Callers(Validator, ".ctor").Should().Equal("LearnStack.Tools.IdentityAuthorityRebind.RebindTool.RunAdmittedAsync");
    }

    [Fact]
    public void Only_The_Rebind_Run_And_The_Two_Freeze_Readers_Take_The_Rebind_Lock()
    {
        // ADR-0104 § 5: the exclusive form belongs to the rebind run, and the shared form to authority qualification, the
        // User-creation admission and the run's own reconciliation of an unknown COMMIT, which waits for the original
        // transaction behind it. A further taker is a further party that can wait on, or hold, the freeze.
        Callers(typeof(IdentityAdvisoryLockKey).FullName!, nameof(IdentityAdvisoryLockKey.ForAuthorityRebind))
            .Should().Equal(
                "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork.AcquireGlobalIdentitySubjectLockAsync",
                "LearnStack.Modules.Identity.Infrastructure.Bindings.IdentityAuthorityQualification.QualifyCoreAsync",
                RebindRun + ".CommitAsync",
                RebindRun + ".RunCoreAsync");
    }

    [Fact]
    public void A_new_minter_or_a_borrowed_core_edge_is_rejected()
    {
        var types = new[] { typeof(BorrowedDescriptorSource), typeof(BorrowedCreationSource) };
        types.Count(type => type.IsSubclassOf(typeof(GlobalIdentityOwnerDescriptor.MintingSource))
            && type.FullName != BindingService).Should().Be(1);
        types.Count(type => type.IsSubclassOf(typeof(GlobalIdentityCreationReference.MintingSource))
            && type.FullName != GlobalSource).Should().Be(1);
        using var module = ModuleDefinition.ReadModule(typeof(IdentityAuditConventionTests).Assembly.Location);
        CoreEdgeViolations(module.GetType(typeof(BorrowedCoreEdge).FullName!.Replace('+', '/')))
            .Should().ContainSingle().Which.Should().Contain(nameof(AuditExecutionCoordinator));
    }

    [Fact]
    public void The_enrollment_reader_has_no_public_mutator_and_does_not_allocate_a_unit()
    {
        typeof(IGlobalIdentityEnrollment).GetMembers().Select(member => member.Name)
            .Should().BeEquivalentTo("IsActive", "get_IsActive");
        typeof(GlobalIdentityEnrollment).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.Name).Should().Equal("get_IsActive");
        typeof(GlobalIdentityEnrollment).GetConstructors().Should().ContainSingle()
            .Which.GetParameters().Should().BeEmpty();
        using var module = ModuleDefinition.ReadModule(typeof(GlobalIdentityEnrollment).Assembly.Location);
        var reader = module.GetType(typeof(GlobalIdentityEnrollment).FullName!);
        Il.Methods(reader).SelectMany(method => method.Definition.HasBody
                ? method.Definition.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>() : [])
            .Where(method => method.Name is "GetRequiredService" or "CreateConnection" or "Open" or "OpenAsync")
            .Should().BeEmpty();
        Callers(typeof(GlobalIdentityEnrollment).FullName!, "Attach")
            .Should().Equal(typeof(NpgsqlUnitOfWork).FullName + ".ctor");
    }

    private static IEnumerable<string> CoreEdgeViolations(TypeDefinition type)
    {
        foreach (var reference in Il.ReferencedTypeReferences(type)
                     .Where(reference => reference is not GenericParameter)
                     .Where(reference => reference.Scope.Name is "LearnStack.Application" or "LearnStack.Infrastructure.Audit")
                     .DistinctBy(reference => reference.FullName))
        {
            var allowed = type.FullName == BindingService && reference.FullName is
                "LearnStack.Application.Audit.AuditExecutionCoordinator"
                or "LearnStack.Application.Persistence.TransactionExecutionCoordinator"
                or "LearnStack.Application.Persistence.TransactionExecutionCoordinator/PreparedGlobalIdentityOwner"
                || type.FullName == InitialMigration && reference.FullName == Installer
                || type.FullName == RebindDdl && reference.FullName == RebindInstaller
                || type.FullName is InvitationJobStore or InvitationStager or DataRequestJobStore
                    && reference.FullName is InvitationProducer or InvitationJobOrigin
                || type.FullName == InvitationRegistration && reference.FullName == FrameworkRequests;
            if (!allowed) yield return type.FullName + " -> " + reference.FullName;
        }
        if (type.FullName == InvitationRegistration)
        {
            var frameworkCalls = Il.Methods(type).Where(pair => pair.Definition.HasBody)
                .SelectMany(pair => pair.Definition.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
                    .Select(instruction => (pair.DeclaredAs, Instruction: instruction, Target: (MethodReference)instruction.Operand)))
                .Where(site => site.Target.DeclaringType.Scope.Name == "LearnStack.Application").ToArray();
            if (frameworkCalls.Length != 1 || frameworkCalls[0].DeclaredAs != "CreateRegistry"
                || frameworkCalls[0].Instruction.OpCode != OpCodes.Call || frameworkCalls[0].Target.DeclaringType.FullName != FrameworkRequests
                || frameworkCalls[0].Target.Name != "get_All" || frameworkCalls[0].Target.Parameters.Count != 0 || frameworkCalls[0].Target.HasThis)
                yield return "Invitation registration imports only the exact construction-free framework request list once";
            yield break;
        }
        if (type.FullName is not (InvitationJobStore or InvitationStager or DataRequestJobStore)) yield break;
        var owner = type.FullName == InvitationStager ? "StageAsync" : "RequireCurrentJob";
        var calls = Il.Methods(type).Where(pair => pair.Definition.HasBody)
            .SelectMany(pair => pair.Definition.Body.Instructions.Where(instruction => instruction.Operand is MethodReference)
                .Select(instruction => (pair.DeclaredAs, Instruction: instruction, Target: (MethodReference)instruction.Operand)))
            .Where(site => site.Target.DeclaringType.Scope.Name == "LearnStack.Application").ToArray();
        foreach (var name in new[] { "CaptureJobOrigin", "RequireJobOrigin" })
            if (calls.Count(site => site.DeclaredAs == owner && site.Target.DeclaringType.FullName == InvitationProducer
                    && site.Target.Name == name && site.Instruction.OpCode == OpCodes.Callvirt) != 1)
                yield return type.FullName + " requires its exact producer member " + name;
        foreach (var call in calls)
            if (call.DeclaredAs != owner || call.Instruction.OpCode != OpCodes.Callvirt
                || !(call.Target.DeclaringType.FullName == InvitationProducer && call.Target.Name is "CaptureJobOrigin" or "RequireJobOrigin"
                    || call.Target.DeclaringType.FullName == InvitationProducer + "/JobOrigin"
                        && (type.FullName == InvitationJobStore ? call.Target.Name is "get_ParentJobId" or "get_OperationType"
                            : type.FullName == DataRequestJobStore ? call.Target.Name is "get_ParentJobId" or "get_OperationType" or "get_Announcement"
                            : call.Target.Name is "get_OperationType" or "get_Announcement")))
                yield return type.FullName + " acquired an unapproved producer member or alias: " + call.Target.FullName;
    }

    private static string[] Callers(string typeName, string methodName) => ProductionAssemblies.All()
        .SelectMany(assembly =>
        {
            using var module = ModuleDefinition.ReadModule(assembly.Location);
            return module.Types.Where(type => type.Name != "<Module>")
                .SelectMany(type => Il.Methods(type)
                    .Where(method => method.Definition.HasBody && method.Definition.Body.Instructions
                        .Any(instruction => instruction.Operand is MethodReference call
                            && call.DeclaringType.FullName == typeName && call.Name == methodName))
                    .Select(method => type.FullName + "." + method.DeclaredAs.TrimStart('.'))).ToArray();
        }).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private sealed class BorrowedDescriptorSource : GlobalIdentityOwnerDescriptor.MintingSource;
    private sealed class BorrowedCreationSource : GlobalIdentityCreationReference.MintingSource;
    private sealed class BorrowedCoreEdge
    {
        public static Type Named() => typeof(AuditExecutionCoordinator);
    }
}
