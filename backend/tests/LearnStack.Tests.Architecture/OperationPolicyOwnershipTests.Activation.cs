using FluentAssertions;
using LearnStack.Api.Authentication;
using LearnStack.Application.Audit;
using LearnStack.Application.Authorization;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Application.Authorization;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Hosting;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class OperationPolicyOwnershipTests
{
    // Inspect the real registrations without resolving a pool, an unqualified
    // reader or a physical unit. Missing configuration remains untouched.
    private static void AssertLiveComposition()
    {
        var services = new ServiceCollection();
        services.AddLearnStackMediatRPipeline();
        services.AddLearnStackLiveAuthorization(new ConfigurationBuilder().Build(), DeploymentMode.SelfHostedOnline);
        LiveCompositionOffenders(services).Should().BeEmpty();
        foreach (var service in new[] { typeof(IAuthorizationCandidateSource), typeof(IOperationInvocationContext),
                     typeof(IAuthorizationRefusalReporter), typeof(ITenantMembershipReader), typeof(IProtectedOperationAuthorizer),
                     typeof(IAuthorizationEvidenceSessionFactory), typeof(IAuthorizationPolicyProvider) })
        {
            var descriptor = services.Single(item => item.ServiceType == service);
            var index = services.IndexOf(descriptor);
            services.RemoveAt(index);
            try { LiveCompositionOffenders(services).Should().Contain(service.FullName!); }
            finally { services.Insert(index, descriptor); }
            services.Insert(services.Count, descriptor);
            try { LiveCompositionOffenders(services).Should().Contain(service.FullName!, "an additional alias/registration changes actual resolution"); }
            finally { services.RemoveAt(services.Count - 1); }
        }
        LiveCompositionOffenders(services).Should().BeEmpty("restoring the actual composition restores its exact aliases");
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        options.FallbackPolicy.Should().BeSameAs(options.DefaultPolicy);
        options.DefaultPolicy.AuthenticationSchemes.Should().BeEmpty();
        options.DefaultPolicy.Requirements.Should().ContainSingle().Which.Should().BeOfType<DeclaredOperationRequirement>();
    }

    private static List<string> LiveCompositionOffenders(ServiceCollection services)
    {
        var errors = new List<string>();
        Alias(typeof(IAuthorizationCandidateSource), typeof(TenantAuthorizationCandidateSource));
        Alias(typeof(IOperationInvocationContext), typeof(OperationAuthorizationCoordinator));
        Alias(typeof(IAuthorizationRefusalReporter), typeof(AuthorizationEvidenceCoordinator));
        Direct(typeof(ITenantMembershipReader), typeof(LiveTenantMembershipReader), ServiceLifetime.Scoped);
        Direct(typeof(IAuthorizationPolicyProvider), typeof(OperationAuthorizationPolicyProvider), ServiceLifetime.Singleton);
        Factory(typeof(IProtectedOperationAuthorizer), typeof(IdentityOperationAuthorizer), ServiceLifetime.Scoped);
        var deferred = typeof(LiveAuthorizationRegistration).GetNestedType("DeferredEvidenceSessions", System.Reflection.BindingFlags.NonPublic)!;
        Factory(typeof(IAuthorizationEvidenceSessionFactory), deferred, ServiceLifetime.Scoped);
        foreach (var source in new[] { typeof(IdentityAuthorizationReadSource), typeof(AuthorizationEvidenceSource) })
            Factory(source, source, ServiceLifetime.Singleton);
        if (services.Count(descriptor => descriptor.ServiceType == typeof(IAuthorizationHandler)
                && descriptor.ImplementationType == typeof(OperationAuthorizationHandler) && descriptor.Lifetime == ServiceLifetime.Scoped) != 1)
            errors.Add(typeof(OperationAuthorizationHandler).FullName!);
        if (services.Count(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(TenantAuthorizationSourcesStartupService) && descriptor.Lifetime == ServiceLifetime.Singleton) != 1)
            errors.Add(typeof(TenantAuthorizationSourcesStartupService).FullName!);
        return errors;

        void Direct(Type service, Type implementation, ServiceLifetime lifetime)
        {
            var descriptors = services.Where(descriptor => descriptor.ServiceType == service).ToArray();
            if (descriptors.Length != 1 || descriptors[0].Lifetime != lifetime || descriptors[0].ImplementationType != implementation)
                errors.Add(service.FullName!);
        }
        void Alias(Type service, Type implementation)
        {
            var descriptors = services.Where(descriptor => descriptor.ServiceType == service).ToArray();
            if (descriptors.Length != 1 || descriptors[0].Lifetime != ServiceLifetime.Scoped
                || !FactoryBody(descriptors[0], body => body.Body.Instructions.Select(instruction => instruction.OpCode.Code)
                        .SequenceEqual([body.IsStatic ? Code.Ldarg_0 : Code.Ldarg_1, Code.Call, Code.Ret])
                    && body.Body.Instructions[1].Operand is GenericInstanceMethod call
                    && call.DeclaringType.FullName == "Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions"
                    && call.Name == "GetRequiredService" && call.GenericArguments.Count == 1
                    && call.GenericArguments[0].FullName == implementation.FullName))
                errors.Add(service.FullName!);
        }
        void Factory(Type service, Type implementation, ServiceLifetime lifetime)
        {
            var descriptors = services.Where(descriptor => descriptor.ServiceType == service).ToArray();
            if (descriptors.Length != 1 || descriptors[0].Lifetime != lifetime
                || !FactoryBody(descriptors[0], body => body.Body.Instructions.Count(instruction => instruction.OpCode == OpCodes.Newobj
                    && instruction.Operand is MethodReference constructor && constructor.DeclaringType.FullName == implementation.FullName!.Replace('+', '/')) == 1))
                errors.Add(service.FullName!);
        }
    }

    private static bool FactoryBody(ServiceDescriptor descriptor, Func<MethodDefinition, bool> predicate)
    {
        if (descriptor.ImplementationFactory is not { } factory) return false;
        using var module = ModuleDefinition.ReadModule(factory.Method.Module.FullyQualifiedName);
        var owner = module.GetType(factory.Method.DeclaringType!.FullName!.Replace('+', '/'));
        return owner.Methods.SingleOrDefault(method => method.Name == factory.Method.Name) is { HasBody: true } body && predicate(body);
    }
}
