using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.SharedKernel.Identity;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class ProviderEventStorageConventionTests
{
    [Fact]
    public void Provider_recipient_is_immutable_and_has_one_qualified_machine_minter()
    {
        var witness = typeof(ProviderEventRecipient);
        witness.IsSealed.Should().BeTrue();
        witness.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Should().ContainSingle().Which.IsPrivate.Should().BeTrue();
        witness.GetProperties().Should().OnlyContain(property => property.SetMethod == null);
        ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && typeof(ProviderEventRecipient.MintingSource).IsAssignableFrom(type))
            .Should().Equal(typeof(ProviderEventIntakeSource));
        typeof(ProviderEventIntakeSource).IsSealed.Should().BeTrue();
        typeof(ProviderEventIntakeSource).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => !method.IsSpecialName).Select(method => method.Name)
            .Should().BeEquivalentTo("QualifyAsync", "AcceptAsync", "ClaimAsync", "RetireAsync", "DisposeAsync",
                "ReadScanAsync", "BeginSweepAsync", "AdvanceSweepAsync", "RecordPageProblemAsync", "TakeProblemAsync",
                "ReadPendingFactAsync", "UpdateSourcePolicyAsync", "ReadHealthAsync");
    }

    [Fact]
    public void Compiled_witness_minting_and_binding_callers_are_closed_and_detect_a_rogue_caller()
    {
        const string witness = "LearnStack.SharedKernel.Identity.ProviderEventRecipient";
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try
        {
            string[] Callers(string owner, string member) => modules.SelectMany(module => module.Types).SelectMany(Flatten)
                .Where(type => !type.Name.Contains('<', StringComparison.Ordinal)
                    && !type.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CompilerGeneratedAttribute"))
                .SelectMany(type => Il.Methods(type).Where(method => method.Definition.HasBody)
                    .SelectMany(method => method.Definition.Body.Instructions
                        .Where(instruction => instruction.Operand is MethodReference target
                            && target.DeclaringType.FullName == owner && target.Name == member)
                        .Select(_ => type.FullName + "." + method.DeclaredAs))).Distinct().ToArray();
            Callers(witness, ".ctor").Should().Equal(witness + "/MintingSource.FromStoredClaim");
            Callers(witness + "/MintingSource", "FromStoredClaim")
                .Should().Equal("LearnStack.Infrastructure.Identity.ProviderEvents.ProviderEventIntakeSource.ClaimAsync");
            Callers(witness, "TryBind").Should().Equal("LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork.SetTenantContextCoreAsync");
            var shared = modules.Single(module => module.Assembly.Name.Name == "LearnStack.SharedKernel");
            var target = shared.Types.Single(type => type.FullName == witness).Methods.Single(method => method.Name == "TryBind");
            var rogue = new TypeDefinition("ReviewProbe", "ProviderBinding", Mono.Cecil.TypeAttributes.Class, shared.TypeSystem.Object);
            var bind = new MethodDefinition("Bind", Mono.Cecil.MethodAttributes.Static, shared.TypeSystem.Void);
            bind.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
            bind.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            rogue.Methods.Add(bind); shared.Types.Add(rogue);
            Callers(witness, "TryBind").Should().Contain("ReviewProbe.ProviderBinding.Bind");
            shared.Types.Remove(rogue);
            Callers(witness, "TryBind").Should().ContainSingle();
        }
        finally { foreach (var module in modules) module.Dispose(); }
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) =>
        new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
}
