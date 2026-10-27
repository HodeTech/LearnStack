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
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class IdentityAuditConventionTests
{
    private const string BindingService = "LearnStack.Modules.Identity.Infrastructure.Bindings.GlobalIdentityBindingService";
    private const string InitialMigration = "LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations.create_identity_schema";
    private const string Installer = "LearnStack.Infrastructure.Audit.IdentityCreationWitnessInstaller";
    private const string GlobalSource = "LearnStack.Application.Audit.AuditExecutionCoordinator+GlobalCreationSource";

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
            .Should().Equal(BindingService + ".AdoptAsync");
        Callers(typeof(TransactionExecutionCoordinator.PreparedGlobalIdentityOwner).FullName!.Replace('+', '/'), "AdmitSerializedAbsence")
            .Should().Equal(BindingService + ".AdoptAsync");
        Callers(typeof(TransactionExecutionCoordinator.PreparedGlobalIdentityOwner).FullName!.Replace('+', '/'), "CompleteExistingAsync")
            .Should().Equal(BindingService + ".AdoptAsync");
        Callers(typeof(IAuditStore).FullName!, nameof(IAuditStore.WriteGlobalIdentityPendingAsync))
            .Should().Equal(typeof(TransactionExecutionCoordinator).FullName + ".ExecuteFrameAsync");
        Callers(typeof(IAuditStore).FullName!, nameof(IAuditStore.ReconcileGlobalIdentityAsync))
            .Should().Equal(typeof(AuditExecutionCoordinator).FullName + ".ReconcileAsync");

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
                || type.FullName == InitialMigration && reference.FullName == Installer;
            if (!allowed) yield return type.FullName + " -> " + reference.FullName;
        }
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
