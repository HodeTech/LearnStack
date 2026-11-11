using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.Modules.Audit.Application.ProviderEvents;
using LearnStack.Modules.Identity.Application.Contracts.ProviderEvents;
using LearnStack.Modules.Identity.Application.ProviderEvents;
using LearnStack.Modules.Identity.Infrastructure.ProviderEvents;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identity;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed class ProviderEventFlowConventionTests
{
    /// <summary>ADR-0097: the provider, machine connector and recipient binding remain a closed framework boundary.</summary>
    [Fact]
    public void Provider_Event_Ingress_Is_Authenticated_And_Confined()
    {
        var types = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes()).ToArray();
        var provider = types.Single(type => !type.IsAbstract && typeof(IIdentityEventProvider).IsAssignableFrom(type));
        provider.FullName.Should().Be("LearnStack.Infrastructure.Identity.ProviderEvents.KeycloakIdentityEventProvider");
        provider.IsSealed.Should().BeTrue(); provider.IsNotPublic.Should().BeTrue();
        typeof(IIdentityEventProvider).GetMethods().Select(method => method.Name).Should().BeEquivalentTo("ReadPolicyAsync", "ReadAsync");
        types.Where(type => !type.IsAbstract && typeof(IProviderEventTargetPublisher).IsAssignableFrom(type))
            .Should().Equal(typeof(ProviderEventTargetPublisher));
        types.Where(type => !type.IsAbstract && typeof(IProviderEventTargetStore).IsAssignableFrom(type))
            .Should().Equal(typeof(ProviderEventTargetStore));
        var source = typeof(ProviderEventIntakeSource);
        source.GetMethods(BindingFlags.Public | BindingFlags.Instance).SelectMany(method => method.GetParameters())
            .Should().NotContain(parameter => typeof(Delegate).IsAssignableFrom(parameter.ParameterType),
                "the private machine source cannot lend a connector or arbitrary callback to callers");
        var binding = types.Single(type => type.FullName == "LearnStack.Api.Identity.ProviderEventProductionBinding");
        binding.IsSealed.Should().BeTrue(); binding.IsNotPublic.Should().BeTrue();
        binding.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Should().BeEmpty();
        using var census = new CallCensus();
        const string intake = "LearnStack.Infrastructure.Identity.ProviderEvents.ProviderEventIntakeSource";
        const string scanner = "LearnStack.Infrastructure.Identity.ProviderEvents.ProviderEventScanner.";
        const string runtime = "LearnStack.Api.Identity.ProviderEventMirrorService.";
        census.Callers(intake, "AcceptAsync").Should().BeEquivalentTo([scanner + "AcceptItemAsync", scanner + "RetryOneAsync"],
            "Fix: only the bounded authenticated scanner and its retained-fact replay may admit global observations");
        census.Callers(intake, "QualifyAsync").Should().Equal(runtime + "QualifyAsync");
        census.Callers(intake, "RequireBusinessDatabaseAsync").Should().Equal(intake + ".QualifyCoreAsync");
        census.Callers(intake, "ClaimAsync").Should().Equal(runtime + "ExecuteAsync");
        census.Callers(intake, "RecordPageProblemAsync").Should().Equal(scanner + "ReadAndAcceptAsync");
        census.Callers("LearnStack.SharedKernel.Identity.IIdentityEventProvider", "ReadAsync")
            .Should().BeEquivalentTo([scanner + "ReadAndAcceptAsync", scanner + "RetryOneAsync"]);
        census.Callers("LearnStack.SharedKernel.Identity.IIdentityEventProvider", "ReadPolicyAsync").Should().Equal(runtime + "ReadSourceSliceAsync");
        census.Callers("LearnStack.Modules.Identity.Application.Contracts.ProviderEvents.IProviderEventTargetPublisher", "ProduceAsync")
            .Should().Equal(runtime + "ProduceAsync");
    }

    /// <summary>ADR-0097: only the real tenant subscriber attaches the central provider observation.</summary>
    [Fact]
    public void Provider_Event_Mirror_Is_Attributed_And_Idempotent()
    {
        var types = ProductionAssemblies.All().SelectMany(assembly => assembly.GetTypes()).ToArray();
        types.Where(type => !type.IsAbstract && typeof(ProviderEventAuditEvidence.MintingSource).IsAssignableFrom(type))
            .Should().Equal(typeof(ProviderEventObservationHandler));
        types.Where(type => !type.IsAbstract && typeof(IProviderEventAuditFactReader).IsAssignableFrom(type))
            .Should().Equal(typeof(ProviderEventTargetStore));
        typeof(ProviderEventAuditEvidence).GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Should().ContainSingle().Which.IsPrivate.Should().BeTrue();
        typeof(ProviderEventAuditEvidence).GetProperties().Should().OnlyContain(property => property.SetMethod == null);
        using var census = new CallCensus();
        census.Callers("LearnStack.SharedKernel.Audit.IAuditStateCapture", "AttachProviderEvent")
            .Should().Equal("LearnStack.Modules.Audit.Application.ProviderEvents.ProviderEventObservationHandler.HandleAsync");
        census.Callers("LearnStack.Api.Identity.ProviderEventProductionBinding", "Bind")
            .Should().Equal("LearnStack.Api.Identity.ProviderEventMirrorService.ProduceAsync");
        census.Callers("LearnStack.SharedKernel.Audit.ProviderEventAuditEvidence/MintingSource", "Observe")
            .Should().Equal("LearnStack.Modules.Audit.Application.ProviderEvents.ProviderEventObservationHandler.HandleAsync");
        census.Callers("LearnStack.Modules.Identity.Application.Contracts.ProviderEvents.IProviderEventAuditFactReader", "ReadAsync")
            .Should().Equal("LearnStack.Modules.Audit.Application.ProviderEvents.ProviderEventObservationHandler.HandleAsync");
        census.Callers("LearnStack.SharedKernel.Audit.ProviderEventAuditEvidence", "BindTo")
            .Should().Equal("LearnStack.Infrastructure.Audit.AuditStateCapture.AttachProviderEvent");
        var module = census.Modules.Single(item => item.Assembly.Name.Name == "LearnStack.SharedKernel");
        var target = module.Types.Single(type => type.FullName == "LearnStack.SharedKernel.Audit.IAuditStateCapture")
            .Methods.Single(method => method.Name == "AttachProviderEvent");
        var rogue = new TypeDefinition("ReviewProbe", "UnownedProviderAudit", Mono.Cecil.TypeAttributes.Class, module.TypeSystem.Object);
        var method = new MethodDefinition("Attach", Mono.Cecil.MethodAttributes.Static, module.TypeSystem.Void);
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); rogue.Methods.Add(method); module.Types.Add(rogue);
        census.Callers(target.DeclaringType.FullName, target.Name).Should().Contain("ReviewProbe.UnownedProviderAudit.Attach");
        module.Types.Remove(rogue);
    }

    private sealed class CallCensus : IDisposable
    {
        internal ModuleDefinition[] Modules { get; } = ProductionAssemblies.All().Select(assembly => ModuleDefinition.ReadModule(assembly.Location)).ToArray();
        internal string[] Callers(string owner, string member) => Modules.SelectMany(module => module.Types).SelectMany(Flatten)
            .Where(type => !type.Name.Contains('<', StringComparison.Ordinal)
                && !type.CustomAttributes.Any(attribute => attribute.AttributeType.Name == "CompilerGeneratedAttribute"))
            .SelectMany(type => Il.Methods(type).Where(method => method.Definition.HasBody)
                .SelectMany(method => method.Definition.Body.Instructions.Where(instruction => instruction.Operand is MethodReference reference
                    && reference.DeclaringType.FullName == owner && reference.Name == member)
                    .Select(_ => type.FullName + "." + WrittenMethod(method.Definition, method.DeclaredAs)))).Distinct().Order(StringComparer.Ordinal).ToArray();
        private static string WrittenMethod(MethodDefinition method, string fallback)
        {
            // Async lambdas live inside display classes, whose numeric name does
            // not identify their source method. Keep attribution to the method
            // encoded in the lambda/state-machine name, not that display class.
            foreach (var name in new[] { method.Name, method.DeclaringType.Name })
            {
                var match = System.Text.RegularExpressions.Regex.Match(name, "<([A-Za-z_][A-Za-z0-9_]*)>",
                    System.Text.RegularExpressions.RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                if (match.Success) return match.Groups[1].Value;
            }
            return fallback;
        }
        private static IEnumerable<TypeDefinition> Flatten(TypeDefinition type) => new[] { type }.Concat(type.NestedTypes.SelectMany(Flatten));
        public void Dispose() { foreach (var module in Modules) module.Dispose(); }
    }
}
