using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class ConsentSubjectConventionTests
{
    [Fact]
    public void Consent_entry_policies_and_current_unactivated_source_have_closed_owners()
    {
        var types = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes()).ToArray();
        var policies = new TenancyOperationPolicySource().Policies;
        policies.Where(policy => policy.Kind == OperationPolicyKind.OwnConsentEntry).Select(policy => policy.RequestType)
            .Should().BeEquivalentTo([typeof(WithdrawOwnMembershipFieldConsentCommand), typeof(ListOwnFieldConsentScopesQuery),
                typeof(ListOwnMembershipFieldConsentsQuery)]);
        policies.Where(policy => policy.Kind == OperationPolicyKind.OwnFieldConsentGrant).Select(policy => policy.RequestType)
            .Should().Equal(typeof(GrantOwnMembershipFieldConsentCommand));
        types.Where(type => !type.IsAbstract && typeof(ConsentSubjectScopeWitness.MintingSource).IsAssignableFrom(type))
            .Should().BeEmpty("the authenticated source is not activated in this step");
        types.Where(type => !type.IsAbstract && typeof(IConsentSubjectScopeSource).IsAssignableFrom(type))
            .Should().BeEmpty("no production source is registered until its owning entry point arrives");
        types.Where(type => !type.IsAbstract && typeof(IConsentSubjectUnitOfWork).IsAssignableFrom(type))
            .Should().Equal(typeof(NpgsqlUnitOfWork));
        typeof(ConsentSubjectScopeWitness).IsSealed.Should().BeTrue();
        typeof(ConsentSubjectScopeWitness).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().ContainSingle().Which.IsPrivate.Should().BeTrue();
        typeof(ConsentSubjectScopeWitness).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
    }

    [Fact]
    public void Consent_witness_callers_are_closed_and_detect_an_extra_minter_or_binding_caller()
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try
        {
            const string witness = "LearnStack.SharedKernel.Identity.ConsentSubjectScopeWitness";
            const string unit = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
            string[] Callers(string targetType, string member) => modules.SelectMany(module => module.Types)
                .SelectMany(Flatten).Where(type => !type.Name.Contains('<', StringComparison.Ordinal)
                    && !type.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CompilerGeneratedAttribute"))
                .SelectMany(type => Il.Methods(type).Where(method => method.Definition.HasBody)
                    .SelectMany(method => method.Definition.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                        && target.DeclaringType.FullName == targetType && target.Name == member)
                        .Select(_ => type.FullName + "." + method.DeclaredAs))).Distinct().ToArray();
            Callers(witness + "/MintingSource", "Capture").Should().BeEmpty();
            Callers(witness, ".ctor").Should().Equal(witness + "/MintingSource.Capture");
            Callers(witness, "TryBindTo").Should().Equal(unit + ".SetTenantContextCoreAsync");
            Callers(witness, "IsBoundTo").Should().BeEquivalentTo(unit + ".SetTenantContextCoreAsync",
                unit + ".ValidateOperationReadScope", unit + ".IsConsentSubjectOwner");
            Callers("LearnStack.SharedKernel.Persistence.IConsentSubjectUnitOfWork", "BeginConsentSubjectTransactionAsync")
                .Should().Equal("LearnStack.Application.Persistence.TransactionExecutionCoordinator.ExecuteCoreAsync");

            var module = modules.Single(item => item.Assembly.Name.Name == "LearnStack.SharedKernel");
            var witnessType = module.Types.SelectMany(Flatten).Single(type => type.FullName == witness);
            var minter = witnessType.NestedTypes.Single(type => type.Name == "MintingSource");
            TypeDefinition[] Minters() => modules.SelectMany(item => item.Types).SelectMany(Flatten)
                .Where(type => !type.IsAbstract && type.BaseType?.FullName == minter.FullName).ToArray();
            Minters().Should().BeEmpty();
            var rogueMinter = new TypeDefinition("ReviewProbe", "ConsentMinter", Mono.Cecil.TypeAttributes.Class, minter);
            module.Types.Add(rogueMinter);
            Minters().Should().ContainSingle().Which.FullName.Should().Be("ReviewProbe.ConsentMinter");
            module.Types.Remove(rogueMinter);
            Minters().Should().BeEmpty();
            var target = witnessType.Methods.Single(method => method.Name == "TryBindTo");
            var probe = new TypeDefinition("ReviewProbe", "ConsentBinding", Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
            var method = new MethodDefinition("Bind", Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            probe.Methods.Add(method); module.Types.Add(probe);
            Callers(witness, "TryBindTo").Should().Contain("ReviewProbe.ConsentBinding.Bind");
            module.Types.Remove(probe);
            Callers(witness, "TryBindTo").Should().Equal(unit + ".SetTenantContextCoreAsync");
        }
        finally { foreach (var module in modules) module.Dispose(); }
    }

    [Fact]
    public void Consent_services_are_scoped_in_both_composition_roots_without_a_witness_source()
    {
        foreach (var (relative, add) in new[]
        {
            ("LearnStack.Api/Composition/PersistenceCompositionExtensions.cs", "TryAddScoped"),
            ("LearnStack.Tools.Seeder/SeedComposition.cs", "AddScoped"),
        })
        {
            var source = File.ReadAllText(Path.Combine(SourceScan.SourceRoot, relative));
            ConsentRegistrations(source).Should().BeEquivalentTo(ExpectedRegistrations(add));
            var dirty = source + "\nclass Rogue { void Add(dynamic services) { services.AddSingleton<IConsentSubjectScopeSource, RogueSource>(); } }";
            ConsentRegistrations(dirty).Should().NotBeEquivalentTo(ExpectedRegistrations(add),
                "an extra witness source must remain visible to the registration census");
            var wrongLifetime = source.Replace(add + "<OwnConsentEntryAuthorization>",
                "AddSingleton<OwnConsentEntryAuthorization>", StringComparison.Ordinal);
            wrongLifetime.Should().NotBe(source);
            ConsentRegistrations(wrongLifetime).Should().NotBeEquivalentTo(ExpectedRegistrations(add));
        }
    }

    private static string[] ExpectedRegistrations(string add) =>
    [
        add + "<OwnFieldConsentAuthorization>", add + "<OwnConsentEntryAuthorization>",
        add + "<IConsentSubjectScopeLookup,ConsentSubjectScopeLookup>",
    ];

    private static string[] ConsentRegistrations(string source) => CSharpSyntaxTree.ParseText(source).GetRoot()
        .DescendantNodes().OfType<InvocationExpressionSyntax>()
        .Select(call => call.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax generic } ? generic : null)
        .Where(generic => generic is not null && generic.Identifier.ValueText.Contains("Add", StringComparison.Ordinal)
            && generic.TypeArgumentList.Arguments.Any(argument => argument.ToString() is "OwnFieldConsentAuthorization"
                or "OwnConsentEntryAuthorization" or "IConsentSubjectScopeLookup" or "IConsentSubjectScopeSource"))
        .Select(generic => generic!.Identifier.ValueText + "<" + string.Join(",", generic.TypeArgumentList.Arguments.Select(argument => argument.ToString())) + ">")
        .ToArray();

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
}
