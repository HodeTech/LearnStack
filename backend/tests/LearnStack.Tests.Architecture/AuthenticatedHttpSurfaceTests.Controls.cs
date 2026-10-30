using FluentAssertions;
using MediatR;
using LearnStack.Modules.Tenancy.Application.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private static void AssertProtectedPolicyControls(Dictionary<string, TypeDefinition> map)
    {
        foreach (var endpoint in Endpoints.Where(entry => entry.Policy is not null))
        {
            var controller = map["LearnStack.Api:" + endpoint.Controller];
            var action = controller.Methods.Single(method => method.Name == endpoint.Action);
            var authorization = Attributes(controller, map).Concat(action.CustomAttributes)
                .Single(attribute => attribute.AttributeType.FullName == typeof(AuthorizeAttribute).FullName);
            var policy = authorization.Properties.Single(property => property.Name == nameof(AuthorizeAttribute.Policy));
            AuthorizationOffenders(controller, action, map).Should().BeEmpty();
            authorization.Properties.Remove(policy);
            try
            {
                AuthorizationOffenders(controller, action, map).Should().ContainSingle("the named live policy is required, not optional metadata");
                authorization.Properties.Add(new CustomAttributeNamedArgument(nameof(AuthorizeAttribute.Policy),
                    new CustomAttributeArgument(controller.Module.TypeSystem.String, "LearnStackOperation:unregistered.read")));
                try { AuthorizationOffenders(controller, action, map).Should().ContainSingle("another named policy cannot substitute for the exact operation"); }
                finally { authorization.Properties.RemoveAt(authorization.Properties.Count - 1); }
            }
            finally { authorization.Properties.Add(policy); }
            AuthorizationOffenders(controller, action, map).Should().BeEmpty("restoring the exact policy restores the approved action");
        }
    }

    private static void AssertPublicAuthorizationControls(Dictionary<string, TypeDefinition> map)
    {
        foreach (var controller in map.Values.Where(type => type.FullName is PublicSite or PublicEducation).ToArray())
        {
            var action = controller.Methods.First(IsAction);
            Authorized(controller, action, map, null).Should().BeTrue();
            var anonymous = controller.CustomAttributes.Single(attribute => attribute.AttributeType.FullName == typeof(AllowAnonymousAttribute).FullName);
            controller.CustomAttributes.Remove(anonymous);
            try { Authorized(controller, action, map, null).Should().BeFalse("public actions require their explicit anonymous metadata"); }
            finally { controller.CustomAttributes.Add(anonymous); }
            foreach (var extra in new[] { Attribute(controller.Module, typeof(AuthorizeAttribute)), Attribute(controller.Module, typeof(AllowAnonymousAttribute)) })
            {
                action.CustomAttributes.Add(extra);
                try { Authorized(controller, action, map, null).Should().BeFalse("public authorization and duplicate anonymous metadata change the closed contract"); }
                finally { action.CustomAttributes.Remove(extra); }
            }
            AuthenticationMetadata.WithCompiledRole(controller.Module, metadata =>
            {
                var definition = (TypeDefinition)metadata.AttributeType;
                map.Add(Key(definition), definition);
                action.CustomAttributes.Add(metadata);
                try { Authorized(controller, action, map, null).Should().BeFalse("derived authorization data also changes a public action"); }
                finally
                {
                    action.CustomAttributes.Remove(metadata);
                    map.Remove(Key(definition));
                }
            });
            Authorized(controller, action, map, null).Should().BeTrue("restored public metadata is the clean control");
        }
    }

    private static void AssertAnonymousMetadataControls(Dictionary<string, TypeDefinition> map)
    {
        foreach (var endpoint in Endpoints)
        {
            var controller = map["LearnStack.Api:" + endpoint.Controller];
            var action = controller.Methods.Single(method => method.Name == endpoint.Action);
            AuthorizationOffenders(controller, action, map, null).Should().BeEmpty();
            foreach (var inherited in new[] { false, true })
                AuthenticationMetadata.WithCompiledAnonymous(controller.Module, inherited, metadata =>
                {
                    var definition = (TypeDefinition)metadata.AttributeType;
                    map.Add(Key(definition), definition);
                    action.CustomAttributes.Add(metadata);
                    try
                    {
                        AuthenticationMetadata.IsAnonymous(metadata.AttributeType, map).Should().BeTrue(
                            "direct and inherited custom anonymous contracts must be recognized");
                        AssertRejected();
                        if (endpoint.Policy is null)
                        {
                            var standard = controller.CustomAttributes.Single(attribute =>
                                attribute.AttributeType.FullName == typeof(AllowAnonymousAttribute).FullName);
                            controller.CustomAttributes.Remove(standard);
                            try { AssertRejected(); }
                            finally { controller.CustomAttributes.Add(standard); }
                        }
                    }
                    finally
                    {
                        action.CustomAttributes.Remove(metadata);
                        map.Remove(Key(definition));
                    }
                    AuthorizationOffenders(controller, action, map, null).Should().BeEmpty(
                        "restoring actual action metadata restores the same authorization predicate");

                    void AssertRejected() => AuthorizationOffenders(controller, action, map, null).Should().ContainSingle()
                        .Which.Should().Be("Unexpected authorization metadata: " + controller.FullName + "." + action.Name,
                            "custom anonymous metadata cannot augment or replace the exact approved authorization contract");
                });
        }
    }

    private static IEnumerable<string> GlobalFilterOffenders(Dictionary<string, TypeDefinition> map)
    {
        // One fresh pass covers filter registration and statically typed provider
        // capabilities. Runtime DI inventory separately covers the composed host.
        var calls = new List<(MethodDefinition Owner, MethodReference Target)>();
        foreach (var type in map.Values)
        {
            var provider = type.BaseType is { } parent && ProviderReference(parent)
                || type.Interfaces.Any(contract => ProviderReference(contract.InterfaceType))
                || type.Fields.Any(field => ProviderReference(field.FieldType))
                || type.Properties.Any(property => ProviderReference(property.PropertyType))
                || type.Events.Any(@event => ProviderReference(@event.EventType))
                || type.GenericParameters.Any(ProviderConstraint);
            foreach (var method in type.Methods)
            {
                provider |= MethodProviderReference(method) || method.GenericParameters.Any(ProviderConstraint);
                if (!method.HasBody) continue;
                provider |= method.Body.Variables.Any(variable => ProviderReference(variable.VariableType));
                foreach (var instruction in method.Body.Instructions)
                    switch (instruction.Operand)
                    {
                        case MethodReference target:
                            provider |= MethodProviderReference(target);
                            if (target.DeclaringType.FullName == "Microsoft.AspNetCore.Mvc.MvcOptions" && target.Name == "get_Filters"
                                || IsFilterCollection(target.DeclaringType))
                                calls.Add((method, target));
                            break;
                        case TypeReference reference:
                            provider |= ProviderReference(reference);
                            break;
                        case FieldReference field:
                            provider |= ProviderReference(field.DeclaringType) || ProviderReference(field.FieldType);
                            break;
                    }
            }
            if (provider)
                yield return "Unexpected MVC filter provider capability: " + type.FullName
                    + "; preserve the sole framework DefaultFilterProvider";
        }
        var reads = calls.Where(site => site.Target.Name == "get_Filters").ToArray();
        var registrations = calls.Where(site => site.Target.Name != "get_Filters").ToArray();
        if (reads.Length != 1 || registrations.Length != 1
            || reads[0].Owner != registrations[0].Owner
            || RootName(reads[0].Owner.DeclaringType) != Versioning
            || !reads[0].Owner.Name.StartsWith("<AddLearnStackApiVersioning>", StringComparison.Ordinal)
            || registrations[0].Target is not GenericInstanceMethod { Name: "Add", Parameters.Count: 0 } registration
            || registration.DeclaringType.FullName != "Microsoft.AspNetCore.Mvc.Filters.FilterCollection"
            || registration.GenericArguments.Count != 1
            || registration.GenericArguments[0].FullName != "LearnStack.Api.Common.ProblemDetailsNormalizationFilter"
            || registration.GenericArguments[0].Scope.Name.Replace(".dll", "", StringComparison.Ordinal) != "LearnStack.Api")
            yield return "Unexpected global MVC filter composition: preserve the sole versioning-owned ProblemDetails normalizer registration";

        static string RootName(TypeDefinition type) => type.DeclaringType is { } parent ? RootName(parent) : type.FullName;
        static bool IsFilterCollection(TypeReference type) => type.FullName == "Microsoft.AspNetCore.Mvc.Filters.FilterCollection"
            || type is GenericInstanceType generic && generic.GenericArguments.Any(argument =>
                argument.FullName == "Microsoft.AspNetCore.Mvc.Filters.IFilterMetadata");

        static bool ProviderReference(TypeReference type) =>
            type.Namespace == "Microsoft.AspNetCore.Mvc.Filters"
                && type.Name is "IFilterProvider" or "FilterProviderContext" or "FilterItem"
            || type is GenericInstanceType generic && generic.GenericArguments.Any(ProviderReference)
            || type is TypeSpecification specification && ProviderReference(specification.ElementType);

        static bool ProviderConstraint(GenericParameter parameter) =>
            parameter.Constraints.Any(constraint => ProviderReference(constraint.ConstraintType));

        static bool MethodProviderReference(MethodReference method) =>
            ProviderReference(method.DeclaringType) || ProviderReference(method.ReturnType)
            || method.Parameters.Any(parameter => ProviderReference(parameter.ParameterType))
            || method is GenericInstanceMethod generic && generic.GenericArguments.Any(ProviderReference);
    }

    private static void AssertFilterProviderControls(Dictionary<string, TypeDefinition> map)
    {
        GlobalFilterOffenders(map).Should().BeEmpty();
        var owner = map.Values.Single(type => type.FullName == Account);
        using var compiled = ModuleDefinition.ReadModule(typeof(SurfaceCompositionProbe).Assembly.Location);
        var probe = compiled.GetType(typeof(SurfaceCompositionProbe).FullName);
        foreach (var name in new[] { nameof(SurfaceCompositionProbe.OrdinaryService), nameof(SurfaceCompositionProbe.Provider) })
        {
            var method = probe.Methods.Single(candidate => candidate.Name == name);
            _ = method.Body.Instructions.Count;
            probe.Methods.Remove(method);
            owner.Methods.Add(method);
            try
            {
                if (name == nameof(SurfaceCompositionProbe.OrdinaryService))
                    GlobalFilterOffenders(map).Should().BeEmpty("ordinary typed DI registration remains legal");
                else
                    GlobalFilterOffenders(map).Should().Contain(error => error.StartsWith("Unexpected MVC filter provider capability:", StringComparison.Ordinal),
                        "compiled generic DI registration can add effective filters without touching MvcOptions.Filters");
            }
            finally { owner.Methods.Remove(method); }
        }
        var provider = compiled.GetType(typeof(SurfaceFilterProvider).FullName);
        _ = provider.Interfaces.Count;
        _ = provider.Properties.Count;
        foreach (var method in provider.Methods)
            if (method.HasBody) _ = method.Body.Instructions.Count;
        compiled.Types.Remove(provider);
        owner.Module.Types.Add(provider);
        map.Add(Key(provider), provider);
        try
        {
            GlobalFilterOffenders(map).Should().Contain(error => error.StartsWith("Unexpected MVC filter provider capability:", StringComparison.Ordinal),
                "a real compiled provider implementation is forbidden even before registration");
        }
        finally
        {
            map.Remove(Key(provider));
            owner.Module.Types.Remove(provider);
        }
        GlobalFilterOffenders(map).Should().BeEmpty("restoring the production census must restore the provider predicate");
    }

    private static void AssertGlobalFilterControls(Dictionary<string, TypeDefinition> map)
    {
        GlobalFilterOffenders(map).Should().BeEmpty();
        var owner = map.Values.Single(type => type.FullName == Account);
        using var compiled = ModuleDefinition.ReadModule(typeof(SurfaceCompositionProbe).Assembly.Location);
        var probe = compiled.GetType(typeof(SurfaceCompositionProbe).FullName);
        foreach (var name in new[] { nameof(SurfaceCompositionProbe.Anonymous), nameof(SurfaceCompositionProbe.Authorization) })
        {
            var method = probe.Methods.Single(candidate => candidate.Name == name);
            _ = method.Body.Instructions.Count;
            probe.Methods.Remove(method);
            owner.Methods.Add(method);
            try
            {
                GlobalFilterOffenders(map).Should().Contain(error => error.StartsWith("Unexpected global MVC filter composition:", StringComparison.Ordinal),
                    "a real compiled registration in a production helper must not evade the runtime environment census");
            }
            finally { owner.Methods.Remove(method); }
        }
        var registration = map.Values.SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions.Select(instruction => (Method: method, Instruction: instruction)))
            .Single(site => site.Instruction.Operand is GenericInstanceMethod { Name: "Add" } target
                && target.GenericArguments.Any(argument => argument.FullName == "LearnStack.Api.Common.ProblemDetailsNormalizationFilter"));
        var index = registration.Method.Body.Instructions.IndexOf(registration.Instruction);
        registration.Method.Body.Instructions.Remove(registration.Instruction);
        try
        {
            GlobalFilterOffenders(map).Should().Contain(error => error.StartsWith("Unexpected global MVC filter composition:", StringComparison.Ordinal),
                "removing the approved registration cannot make the global-filter census vacuous");
        }
        finally { registration.Method.Body.Instructions.Insert(index, registration.Instruction); }
        GlobalFilterOffenders(map).Should().BeEmpty("restoring compiled registration sites restores the same predicate");
    }

    private static void AssertGeneratedHandlerFieldControls(TypeDefinition[] roots, Dictionary<string, TypeDefinition> map)
    {
        var owner = roots.First(type => type.Methods.Any(method => method.Name == "Handle" && method.CustomAttributes.Any(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute")));
        var handle = owner.Methods.Single(method => method.Name == "Handle");
        var state = (TypeReference)handle.CustomAttributes.Single(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute").ConstructorArguments[0].Value;
        var generated = map[Key(state)];
        var target = roots.Single(type => Handles(type, typeof(LearnStack.Modules.Tenancy.Application.Contracts.Account.GetAccountSiteQuery).FullName!));
        var contract = generated.Module.ImportReference(target.Interfaces.Single().InterfaceType);
        NestedDispatchOffenders([owner], map).Should().BeEmpty("the actual generated owner's normal self capture remains legal");
        var unused = new FieldDefinition("UnusedHandlerCapability", FieldAttributes.Private, contract);
        generated.Fields.Add(unused);
        try
        {
            NestedDispatchOffenders([owner], map).Should().Contain("Nested HTTP dispatch/helper handler: " + owner.FullName,
                "an unused handler field on the actual state machine must survive generated-type de-duplication");
        }
        finally { generated.Fields.Remove(unused); }
        var captured = generated.Fields.Single(field => field.Name == "<>4__this");
        var ownType = captured.FieldType;
        captured.FieldType = contract;
        try
        {
            NestedDispatchOffenders([owner], map).Should().Contain("Nested HTTP dispatch/helper handler: " + owner.FullName,
                "the compiler capture spelling alone cannot exempt a foreign handler capability");
        }
        finally { captured.FieldType = ownType; }
        NestedDispatchOffenders([owner], map).Should().BeEmpty("both real generated-field mutations were restored");
    }

    private static void AssertInheritedHandlerControls(TypeDefinition[] roots, Dictionary<string, TypeDefinition> map)
    {
        foreach (var owner in roots.Where(root => root.BaseType is { } parent && map.TryGetValue(Key(parent), out var definition)
                     && definition.IsAbstract).DistinctBy(root => Key(root.BaseType)))
        {
            var parent = map[Key(owner.BaseType)];
            void Dirty() => NestedDispatchOffenders([owner], map).Should().Contain(error =>
                error.StartsWith("Nested HTTP dispatch/helper handler:", StringComparison.Ordinal));
            NestedDispatchOffenders([owner], map).Should().BeEmpty("inherited implementations do not acquire sibling handlers");
            foreach (var holder in new[] { owner, parent })
            {
                var field = new FieldDefinition("AbstractCollaborator", FieldAttributes.Private, parent);
                holder.Fields.Add(field);
                try { Dirty(); } finally { holder.Fields.Remove(field); }
            }
            var constructor = owner.Methods.Single(method => method.IsConstructor && !method.IsStatic);
            var parameter = new ParameterDefinition("collaborator", ParameterAttributes.None, parent);
            constructor.Parameters.Add(parameter);
            try { Dirty(); } finally { constructor.Parameters.Remove(parameter); }
            var handle = owner.Methods.Single(method => method.Name == "Handle");
            var local = new VariableDefinition(parent);
            handle.Body.Variables.Add(local);
            try { Dirty(); } finally { handle.Body.Variables.Remove(local); }
            var token = Instruction.Create(OpCodes.Ldtoken, parent);
            handle.Body.Instructions.Add(token);
            try { Dirty(); } finally { handle.Body.Instructions.Remove(token); }
            var sender = new FieldDefinition("HiddenSender", FieldAttributes.Private, parent.Module.ImportReference(typeof(ISender)));
            parent.Fields.Add(sender);
            try { Dirty(); } finally { parent.Fields.Remove(sender); }
            NestedDispatchOffenders([owner], map).Should().BeEmpty("base collaborators and inherited sender capability were restored");
        }
    }

    private static void AssertRootDispatchControls(Dictionary<string, TypeDefinition> map)
    {
        var roots = HandlerRoots(map).ToArray();
        var owner = roots.Single(type => Handles(type, typeof(LearnStack.Modules.Tenancy.Application.Contracts.PublicReads.GetPublicSiteQuery).FullName!));
        var target = roots.Single(type => Handles(type, typeof(LearnStack.Modules.Tenancy.Application.Contracts.Account.GetAccountSiteQuery).FullName!));
        using var compiled = ModuleDefinition.ReadModule(typeof(SurfaceDispatchProbe).Assembly.Location);
        var probe = compiled.GetType(typeof(SurfaceDispatchProbe).FullName);
        var bridge = compiled.GetType(typeof(ISurfaceHandlerBridge).FullName);
        _ = bridge.Methods.Count;
        compiled.Types.Remove(bridge);
        target.Module.Types.Add(bridge);
        map.Add(Key(bridge), bridge);
        var implementation = compiled.GetType(typeof(SurfaceHandlerBridge).FullName);
        var read = implementation.Methods.Single(method => method.Name == nameof(SurfaceHandlerBridge.Read));
        _ = read.Body.Instructions.Count;
        implementation.Methods.Remove(read);
        target.Methods.Add(read);
        var contract = new InterfaceImplementation(bridge);
        target.Interfaces.Add(contract);
        try
        {
            NestedDispatchOffenders(roots, map).Should().BeEmpty("a root's own interface declaration is not acquisition by another root");
            var clean = probe.Methods.Single(candidate => candidate.Name == nameof(SurfaceDispatchProbe.Clean));
            _ = clean.Body.Instructions.Count;
            probe.Methods.Remove(clean);
            owner.Methods.Add(clean);
            try { NestedDispatchOffenders(roots, map).Should().BeEmpty("ordinary generic service resolution is not handler acquisition"); }
            finally { owner.Methods.Remove(clean); }
            foreach (var name in new[] { nameof(SurfaceDispatchProbe.Interface), nameof(SurfaceDispatchProbe.Generic) })
            {
                var method = probe.Methods.Single(candidate => candidate.Name == name);
                _ = method.Body.Instructions.Count;
                if (name == nameof(SurfaceDispatchProbe.Interface))
                {
                    method.Parameters[0].ParameterType = bridge;
                    foreach (var call in method.Body.Instructions.Select(instruction => instruction.Operand).OfType<MethodReference>()
                                 .Where(call => call.DeclaringType.FullName == bridge.FullName))
                        call.DeclaringType = bridge;
                }
                probe.Methods.Remove(method);
                owner.Methods.Add(method);
                try
                {
                    NestedDispatchOffenders(roots, map).Should().Contain(error => error.StartsWith("Nested HTTP dispatch/helper handler:", StringComparison.Ordinal),
                        "a compiled custom-interface or generic service acquisition cannot reach another approved root");
                }
                finally { owner.Methods.Remove(method); }
                NestedDispatchOffenders(roots, map).Should().BeEmpty();
            }
        }
        finally
        {
            target.Interfaces.Remove(contract);
            target.Methods.Remove(read);
            map.Remove(Key(bridge));
            target.Module.Types.Remove(bridge);
        }
        NestedDispatchOffenders(roots, map).Should().BeEmpty("restored production roots remain legal");
    }
}

internal static class SurfaceCompositionProbe
{
    internal static void Anonymous(MvcOptions options) => options.Filters.Add(new AllowAnonymousFilter());
    internal static void Authorization(MvcOptions options) => options.Filters.Add<SurfaceAuthorizationFilter>();
    internal static void OrdinaryService(IServiceCollection services) => services.AddSingleton<object>();
    internal static void Provider(IServiceCollection services) => services.AddSingleton<IFilterProvider, SurfaceFilterProvider>();
}

internal sealed class SurfaceFilterProvider : IFilterProvider
{
    public int Order => int.MaxValue;

    public void OnProvidersExecuting(FilterProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Results[0].Filter = new SurfaceAuthorizationFilter();
    }

    public void OnProvidersExecuted(FilterProviderContext context) => ArgumentNullException.ThrowIfNull(context);
}

internal sealed class SurfaceAuthorizationFilter : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context) => ArgumentNullException.ThrowIfNull(context);
}

internal interface ISurfaceHandlerBridge
{
    string Read();
}

internal sealed class SurfaceHandlerBridge : ISurfaceHandlerBridge
{
    public string Read() => "read";
}

internal static class SurfaceDispatchProbe
{
    internal static int Clean(IServiceProvider services) => services.GetRequiredService<object>().GetHashCode();
    internal static string Interface(ISurfaceHandlerBridge handler) => handler.Read();
    internal static int Generic(IServiceProvider services) => services.GetRequiredService<GetAccountSiteQueryHandler>().GetHashCode();
}
