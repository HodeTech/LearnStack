using FluentAssertions;
using LearnStack.Modules.Tenancy.Application.Account;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Mono.Cecil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
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

    private static IEnumerable<string> GlobalFilterOffenders(Dictionary<string, TypeDefinition> map)
    {
        var calls = map.Values.SelectMany(type => type.Methods).Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions.Where(instruction => instruction.Operand is MethodReference target
                    && (target.DeclaringType.FullName == "Microsoft.AspNetCore.Mvc.MvcOptions" && target.Name == "get_Filters"
                        || IsFilterCollection(target.DeclaringType)))
                .Select(instruction => (Owner: method, Target: (MethodReference)instruction.Operand))).ToArray();
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
