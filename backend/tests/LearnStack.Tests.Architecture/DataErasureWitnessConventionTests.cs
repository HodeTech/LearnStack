using System.Reflection;
using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0107 § 5: the erasure domain operations run only under the erasure witness, and the witness has one production
/// minter. These rules pin the closed owners: the witness's private construction, its one minting source and the one
/// capture site, and the one service that calls each erasure operation; a planted extra minter or caller is detected.
/// </summary>
public sealed class DataErasureWitnessConventionTests
{
    private const string Witness = "LearnStack.Modules.Identity.Domain.DataErasureWitness";
    private const string Service = "LearnStack.Modules.Identity.Application.DataRequests.DataErasureService";
    private const string Source = Service + "/FulfillingErasure";

    /// <summary>The erasure operations and the one service method that calls each.</summary>
    private static readonly (string Type, string Method, string Caller)[] Operations =
    [
        ("LearnStack.Modules.Identity.Domain.Membership", nameof(Membership.RemoveAssignmentsForErasure), Service + ".EraseMembershipsAsync"),
        ("LearnStack.Modules.Identity.Domain.Membership", nameof(Membership.ClearProfileForErasure), Service + ".EraseMembershipsAsync"),
        ("LearnStack.Modules.Identity.Domain.Membership", nameof(Membership.RevokeForErasure), Service + ".EraseMembershipsAsync"),
        ("LearnStack.Modules.Identity.Domain.Invitation", nameof(Invitation.RevokeForErasure), Service + ".RevokeIssuedInvitationsAsync"),
    ];

    [Fact]
    public void Erasure_witness_has_a_private_construction_and_one_production_minting_source()
    {
        typeof(DataErasureWitness).IsSealed.Should().BeTrue();
        typeof(DataErasureWitness).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().ContainSingle().Which.IsPrivate.Should().BeTrue();
        typeof(DataErasureWitness).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
        ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes())
            .Where(type => !type.IsAbstract && typeof(DataErasureWitness.MintingSource).IsAssignableFrom(type))
            .Select(type => type.FullName!.Replace('+', '/')).Should().Equal(Source);
        foreach (var (type, method, _) in Operations)
            typeof(Membership).Assembly.GetType(type)!.GetMethod(method)!.GetParameters()
                .Should().Contain(parameter => parameter.ParameterType == typeof(DataErasureWitness), $"{method} runs only under the witness");
    }

    /// <summary>The canonical rule: the capture, the construction and each erasure operation have closed callers.</summary>
    [Fact]
    public void Erasure_Operations_Run_Only_Under_The_Erasure_Witness()
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try
        {
            string[] Callers(string targetType, string member) => modules.SelectMany(module => module.Types)
                .SelectMany(Flatten).Where(type => !type.Name.Contains('<', StringComparison.Ordinal)
                    && !type.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CompilerGeneratedAttribute"))
                .SelectMany(type => Il.Methods(type).Where(method => method.Definition.HasBody)
                    .SelectMany(method => method.Definition.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                        && target.DeclaringType.FullName == targetType && target.Name == member)
                        .Select(_ => type.FullName + "." + method.DeclaredAs))).Distinct().ToArray();

            Callers(Witness + "/MintingSource", "Capture").Should().Equal(Source + ".Mint");
            Callers(Witness, ".ctor").Should().Equal(Witness + "/MintingSource.Capture");
            foreach (var (type, method, caller) in Operations)
                Callers(type, method).Should().Equal([caller], $"only the erasure service calls {method}");

            var domain = modules.Single(module => module.Assembly.Name.Name == "LearnStack.Modules.Identity.Domain");
            var membership = domain.Types.Single(type => type.FullName == "LearnStack.Modules.Identity.Domain.Membership");
            var target = membership.Methods.Single(method => method.Name == nameof(Membership.ClearProfileForErasure));
            var probe = new TypeDefinition("ReviewProbe", "ErasureBorrower", Mono.Cecil.TypeAttributes.Class, domain.TypeSystem.Object);
            var borrow = new MethodDefinition("Borrow", Mono.Cecil.MethodAttributes.Static, domain.TypeSystem.Void);
            borrow.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
            borrow.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            probe.Methods.Add(borrow);
            domain.Types.Add(probe);
            Callers("LearnStack.Modules.Identity.Domain.Membership", nameof(Membership.ClearProfileForErasure))
                .Should().Contain("ReviewProbe.ErasureBorrower.Borrow", "a planted caller must stay visible to the census");
            domain.Types.Remove(probe);

            var witness = domain.Types.SelectMany(Flatten).Single(type => type.FullName == Witness);
            var minter = witness.NestedTypes.Single(type => type.Name == "MintingSource");
            var rogue = new TypeDefinition("ReviewProbe", "ErasureMinter", Mono.Cecil.TypeAttributes.Class, minter);
            domain.Types.Add(rogue);
            modules.SelectMany(module => module.Types).SelectMany(Flatten)
                .Where(type => !type.IsAbstract && type.BaseType?.FullName == minter.FullName).Select(type => type.FullName)
                .Should().BeEquivalentTo([Source, "ReviewProbe.ErasureMinter"], "a planted minter must stay visible to the census");
            domain.Types.Remove(rogue);
        }
        finally { foreach (var module in modules) module.Dispose(); }
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
}
