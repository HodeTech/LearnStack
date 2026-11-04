using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationModels;
using Microsoft.AspNetCore.Mvc.Filters;
using Mono.Cecil;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    private static IEnumerable<string> AttachedFilterOffenders(TypeDefinition controller, MethodDefinition action,
        Dictionary<string, TypeDefinition> map)
    {
        var endpoint = Endpoints.SingleOrDefault(entry => entry.Controller == controller.FullName && entry.Action == action.Name);
        var attributes = Attributes(controller, map).Concat(action.CustomAttributes).ToArray();
        var filters = attributes.Where(attribute => AuthenticationMetadata.IsFilterData(attribute.AttributeType, map)).ToArray();
        if (endpoint is null || filters.Length != (endpoint.ProtectedOperation is null ? 2 : 3)
            || filters.Count(IsApiController) != 1 || filters.Count(attribute => IsResponseMetadata(attribute, endpoint)) != 1
            || filters.Count(attribute => IsKeyMetadata(attribute, endpoint)) != (endpoint.ProtectedOperation is null ? 0 : 1))
            yield return "Unexpected attached MVC filter metadata: " + controller.FullName + "." + action.Name
                + "; preserve the exact framework response and finite protected-key metadata";
        var policies = attributes.Where(attribute => attribute.AttributeType.FullName == "LearnStack.Api.Authentication.OperationPolicyAttribute").ToArray();
        if (endpoint is null || policies.Length != 1 || policies[0].AttributeType.Scope.Name.Replace(".dll", "", StringComparison.Ordinal) != "LearnStack.Api"
            || policies[0].Properties.Count != 0 || policies[0].Fields.Count != 0 || policies[0].ConstructorArguments.Count != 1
            || policies[0].ConstructorArguments[0].Value is not TypeReference request
            || Key(request) != endpoint.Request.Assembly.GetName().Name + ":" + endpoint.Request.FullName)
            yield return "Unexpected operation request metadata: " + controller.FullName + "." + action.Name;
    }

    private static bool FrameworkAttribute(CustomAttribute attribute) =>
        attribute.AttributeType.Scope.Name.Replace(".dll", "", StringComparison.Ordinal) == "Microsoft.AspNetCore.Mvc.Core"
        && attribute.Properties.Count == 0 && attribute.Fields.Count == 0;

    private static bool IsApiController(CustomAttribute attribute) => FrameworkAttribute(attribute)
        && attribute.AttributeType.FullName == "Microsoft.AspNetCore.Mvc.ApiControllerAttribute"
        && attribute.ConstructorArguments.Count == 0;

    private static bool IsResponseMetadata(CustomAttribute attribute, Endpoint endpoint) => FrameworkAttribute(attribute)
        && attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is int status && status == endpoint.Status
        && (endpoint.Response is null
            ? attribute.AttributeType.FullName == "Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute"
            : attribute.AttributeType is GenericInstanceType response
                && response.ElementType.FullName == "Microsoft.AspNetCore.Mvc.ProducesResponseTypeAttribute`1"
                && response.GenericArguments.Count == 1
                && SameType(response.GenericArguments[0], attribute.AttributeType.Module.ImportReference(endpoint.Response)));

    private static bool SameType(TypeReference left, TypeReference right) => left.FullName == right.FullName
        && Il.ExpandedTypeReferences(left).Select(Key).SequenceEqual(Il.ExpandedTypeReferences(right).Select(Key), StringComparer.Ordinal);

    private static bool IsKeyMetadata(CustomAttribute attribute, Endpoint endpoint) => endpoint.ProtectedOperation is { } operation
        && Key(attribute.AttributeType) == "LearnStack.Api:" + IdempotentMetadata
        && attribute.ConstructorArguments.Count == 1 && attribute.ConstructorArguments[0].Value is int actual && actual == operation
        && Key(attribute.ConstructorArguments[0].Type) == "LearnStack.Api:LearnStack.Api.Idempotency.ProtectedAdministrationOperation"
        && attribute.Properties.Count == 0 && attribute.Fields.Count == 0;

    private static void AssertAttachedMetadataControls(Dictionary<string, TypeDefinition> map)
    {
        using var compiled = ModuleDefinition.ReadModule(typeof(SurfaceAttachedMetadataProbe).Assembly.Location);
        var probe = compiled.GetType(typeof(SurfaceAttachedMetadataProbe).FullName);
        foreach (var endpoint in Endpoints)
        {
            var controller = map["LearnStack.Api:" + endpoint.Controller];
            var action = controller.Methods.Single(method => method.Name == endpoint.Action);
            AssertClean();
            foreach (var method in probe.Methods.Where(method => method.HasCustomAttributes))
            {
                var source = method.CustomAttributes.Single(attribute => attribute.AttributeType.Namespace != "System.Runtime.CompilerServices");
                var metadata = CopyAttribute(source, controller.Module);
                var requirement = method.Name.Contains("Requirement", StringComparison.Ordinal);
                action.CustomAttributes.Add(metadata);
                try { AssertDirty(); }
                finally { action.CustomAttributes.Remove(metadata); }
                AssertClean();

                // Exercise actual controller inheritance as well as an attribute
                // whose filter/requirement interface is inherited from its base.
                var ancestor = Bases(controller, map).OfType<TypeDefinition>().First();
                ancestor.CustomAttributes.Add(metadata);
                try { AssertDirty(); }
                finally { ancestor.CustomAttributes.Remove(metadata); }
                AssertClean();

                void AssertDirty()
                {
                    if (requirement)
                    {
                        AuthenticationMetadata.IsRequirementData(metadata.AttributeType, map).Should().BeTrue();
                        AuthorizationOffenders(controller, action, map).Should().Contain(error =>
                            error.StartsWith("Unexpected authorization metadata:", StringComparison.Ordinal),
                            "compiled requirement metadata changes the approved authorization contract");
                    }
                    else
                    {
                        AuthenticationMetadata.IsFilterData(metadata.AttributeType, map).Should().BeTrue();
                        AttachedFilterOffenders(controller, action, map).Should().Contain(error =>
                            error.StartsWith("Unexpected attached MVC filter metadata:", StringComparison.Ordinal),
                            "compiled direct, inherited, type-factory and service-factory filters can execute outside action dispatch");
                    }
                }
            }

            // Legal framework metadata is required, not merely exempt from the ban.
            var response = action.CustomAttributes.Single(attribute => attribute.AttributeType.GetElementType().Name
                is "ProducesResponseTypeAttribute" or "ProducesResponseTypeAttribute`1");
            action.CustomAttributes.Remove(response);
            try
            {
                AttachedFilterOffenders(controller, action, map).Should().Contain(error =>
                    error.StartsWith("Unexpected attached MVC filter metadata:", StringComparison.Ordinal));
            }
            finally { action.CustomAttributes.Add(response); }
            AssertClean();

            void AssertClean()
            {
                AttachedFilterOffenders(controller, action, map).Should().BeEmpty();
                AuthorizationOffenders(controller, action, map).Should().BeEmpty();
            }
        }
    }

    private static CustomAttribute CopyAttribute(CustomAttribute source, ModuleDefinition module)
    {
        var copy = new CustomAttribute(module.ImportReference(source.Constructor));
        foreach (var argument in source.ConstructorArguments) copy.ConstructorArguments.Add(argument);
        foreach (var property in source.Properties) copy.Properties.Add(property);
        foreach (var field in source.Fields) copy.Fields.Add(field);
        return copy;
    }

    private static void AssertEndpointConventionControls(ModuleDefinition api)
    {
        using var compiled = ModuleDefinition.ReadModule(typeof(SurfaceEndpointConventionProbe).Assembly.Location);
        var probe = compiled.GetType(typeof(SurfaceEndpointConventionProbe).FullName);
        var owner = api.GetType(Account);
        UnapprovedMappingOffenders(MappingSites([api])).Should().BeEmpty();
        foreach (var method in probe.Methods.Where(method => method.IsStatic).ToArray())
        {
            _ = method.Body.Instructions.Count;
            probe.Methods.Remove(method);
            owner.Methods.Add(method);
            try
            {
                var offenders = UnapprovedMappingOffenders(MappingSites([api])).ToArray();
                if (method.Name == nameof(SurfaceEndpointConventionProbe.Clean))
                    offenders.Should().BeEmpty("ordinary route display naming does not grant authorization capability");
                else
                    offenders.Should().Contain(error => error.StartsWith("Unapproved endpoint mapping:", StringComparison.Ordinal),
                        "ordinary compiled endpoint conventions cannot add unenumerated authorization metadata");
            }
            finally { owner.Methods.Remove(method); }
            UnapprovedMappingOffenders(MappingSites([api])).Should().BeEmpty("restoring production methods restores the same convention predicate");
        }
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal class SurfaceAuthorizationAttribute : Attribute, IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Result = new ForbidResult();
    }
}

internal sealed class SurfaceInheritedAuthorizationAttribute : SurfaceAuthorizationAttribute;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal class SurfaceRequirementAttribute : Attribute, IAuthorizationRequirementData
{
    public IEnumerable<IAuthorizationRequirement> GetRequirements() => [new DenyAnonymousAuthorizationRequirement()];
}

internal sealed class SurfaceInheritedRequirementAttribute : SurfaceRequirementAttribute;

internal sealed class SurfaceArgumentAuthorizationFilter(string marker) : IAuthorizationFilter
{
    public void OnAuthorization(AuthorizationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Result = new ContentResult { Content = marker, StatusCode = 403 };
    }
}

internal static class SurfaceAttachedMetadataProbe
{
    [TypeFilter(typeof(SurfaceArgumentAuthorizationFilter), Arguments = new object[] { "probe" }, Order = 42)]
    internal static void TypeFactory() { }

    [ServiceFilter(typeof(SurfaceAuthorizationFilter), Order = 42)]
    internal static void ServiceFactory() { }

    [SurfaceAuthorization]
    internal static void Direct() { }

    [SurfaceInheritedAuthorization]
    internal static void Inherited() { }

    [SurfaceRequirement]
    internal static void Requirement() { }

    [SurfaceInheritedRequirement]
    internal static void InheritedRequirement() { }
}

internal static class SurfaceEndpointConventionProbe
{
    internal static void Clean(IEndpointConventionBuilder builder) => builder.WithDisplayName("surface");
    internal static void Policy(IEndpointConventionBuilder builder) =>
        builder.RequireAuthorization(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
    internal static void Anonymous(IEndpointConventionBuilder builder) => builder.AllowAnonymous();
    internal static void Metadata(IEndpointConventionBuilder builder) => builder.WithMetadata(new SurfaceRequirementAttribute());
    internal static void Convention(IEndpointConventionBuilder builder) =>
        builder.Add(endpoint => endpoint.Metadata.Add(new SurfaceRequirementAttribute()));
    internal static void FinalConvention(IEndpointConventionBuilder builder) =>
        builder.Finally(endpoint => endpoint.Metadata.Add(new SurfaceRequirementAttribute()));
    internal static void ConcreteConvention(ControllerActionEndpointConventionBuilder builder) =>
        builder.Add(endpoint => endpoint.Metadata.Add(new SurfaceRequirementAttribute()));
    internal static void SelectorMetadata(SelectorModel selector) =>
        selector.EndpointMetadata.Add(new SurfaceRequirementAttribute());
}
