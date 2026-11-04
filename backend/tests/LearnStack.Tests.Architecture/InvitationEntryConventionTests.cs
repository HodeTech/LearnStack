using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Mono.Cecil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class InvitationEntryConventionTests
{
    [Fact]
    public void Invitation_entry_has_exactly_three_policies_and_one_actual_nonmember_source_and_owner()
    {
        var types = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes()).ToArray();
        new IdentityOperationPolicySource().Policies.Where(policy => policy.Kind == OperationPolicyKind.InvitationEntry)
            .Select(policy => policy.RequestType).Should().BeEquivalentTo(
                [typeof(InspectInvitationQuery), typeof(AcceptInvitationCommand), typeof(GetOwnInvitationResultQuery)]);
        types.Where(type => !type.IsAbstract && typeof(InvitationEntryScopeWitness.MintingSource).IsAssignableFrom(type))
            .Should().Equal(typeof(InvitationEntrySource));
        types.Where(type => !type.IsAbstract && typeof(IInvitationEntryScopeSource).IsAssignableFrom(type))
            .Should().Equal(typeof(InvitationEntrySource));
        types.Where(type => !type.IsAbstract && typeof(IInvitationEntryUnitOfWork).IsAssignableFrom(type))
            .Should().Equal(typeof(NpgsqlUnitOfWork));
        typeof(InvitationEntryScopeWitness).IsSealed.Should().BeTrue();
        typeof(InvitationEntryScopeWitness).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Should().ContainSingle().Which.IsPrivate.Should().BeTrue();
        typeof(InvitationEntryScopeWitness).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
    }
    [Fact]
    public void Invitation_witness_actual_mint_bind_and_transaction_callers_are_finite_and_nonempty()
    {
        var modules = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        try
        {
            var sites = modules.SelectMany(module => module.Types).SelectMany(Flatten).Where(type => !type.Name.Contains('<', StringComparison.Ordinal)
                && !type.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CompilerGeneratedAttribute")).SelectMany(type => Il.Methods(type)
                .Where(method => method.Definition.HasBody).SelectMany(method => method.Definition.Body.Instructions
                    .Where(instruction => instruction.Operand is MethodReference)
                    .Select(instruction => (Owner: type.FullName + "." + method.DeclaredAs, Target: (MethodReference)instruction.Operand))))
                .Distinct().ToArray();
            void Exact(string type, string member, params string[] expected) => sites.Where(site => site.Target.DeclaringType.FullName == type
                && site.Target.Name == member).Select(site => site.Owner).Distinct().Should().BeEquivalentTo(expected);
            const string witness = "LearnStack.SharedKernel.Identity.InvitationEntryScopeWitness";
            const string unit = "LearnStack.Infrastructure.Persistence.NpgsqlUnitOfWork";
            Exact(witness + "/MintingSource", "Capture", "LearnStack.Api.Authentication.InvitationEntrySource.ForInvocation");
            Exact(witness, ".ctor", witness + "/MintingSource.Capture");
            Exact(witness, "TryBindTo", unit + ".SetTenantContextCoreAsync");
            Exact(witness, "IsBoundTo", unit + ".SetTenantContextCoreAsync", unit + ".ValidateOperationReadScope", unit + ".IsInvitationEntryOwner");
            Exact("LearnStack.SharedKernel.Persistence.IInvitationEntryUnitOfWork", "BeginInvitationEntryTransactionAsync",
                "LearnStack.Application.Persistence.TransactionExecutionCoordinator.ExecuteCoreAsync");
        }
        finally { foreach (var module in modules) module.Dispose(); }
    }
    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
}
