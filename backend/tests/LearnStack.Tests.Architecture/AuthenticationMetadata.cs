using Microsoft.AspNetCore.Authorization;
using Mono.Cecil;

namespace LearnStack.Tests.Architecture;

internal static class AuthenticationMetadata
{
    private static readonly Lazy<DefaultAssemblyResolver> Resolver = new(() =>
    {
        var resolver = new DefaultAssemblyResolver();
        foreach (var directory in new[] { AppContext.BaseDirectory, Path.GetDirectoryName(typeof(object).Assembly.Location)!,
                     Path.GetDirectoryName(typeof(AuthorizeAttribute).Assembly.Location)! }.Distinct(StringComparer.Ordinal))
            resolver.AddSearchDirectory(directory);
        return resolver;
    });

    internal static bool IsData(TypeReference reference, IReadOnlyDictionary<string, TypeDefinition> types) =>
        Implements(reference, types, "Microsoft.AspNetCore.Authorization.AuthorizeAttribute",
            "Microsoft.AspNetCore.Authorization.IAuthorizeData");

    internal static bool IsAnonymous(TypeReference reference, IReadOnlyDictionary<string, TypeDefinition> types) =>
        Implements(reference, types, "Microsoft.AspNetCore.Authorization.AllowAnonymousAttribute",
            "Microsoft.AspNetCore.Authorization.IAllowAnonymous");

    private static bool Implements(TypeReference reference, IReadOnlyDictionary<string, TypeDefinition> types,
        string standardAttribute, string contractName)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        return Visit(reference);

        bool Visit(TypeReference type)
        {
            if (type.FullName == standardAttribute || type.FullName == contractName) return true;
            if (!visited.Add(Key(type))) return false;
            if (!types.TryGetValue(Key(type), out var definition))
            {
                try
                {
                    // External attributes and inherited interfaces are part of the
                    // framework's authority surface too. An unresolved base cannot
                    // establish absence of authorization metadata.
                    lock (Resolver.Value)
                        definition = new MetadataResolver(Resolver.Value).Resolve(type);
                }
                catch (AssemblyResolutionException) { return true; }
                catch (ResolutionException) { return true; }
                if (definition is null) return true;
            }
            return definition.Interfaces.Any(contract => Visit(contract.InterfaceType))
                || definition.BaseType is { } parent && Visit(parent);
        }
    }

    internal static string Key(TypeReference type) =>
        type.Scope.Name.Replace(".dll", "", StringComparison.Ordinal) + ":" + type.FullName;

    internal static void WithExternalRole(ModuleDefinition api, Action<CustomAttribute> assertion)
    {
        using var probes = ModuleDefinition.ReadModule(typeof(AuthenticationMetadata).Assembly.Location);
        var source = probes.GetType(typeof(CensusAuthorizationProbe).FullName).Methods.Single(method => method.Name == "Read")
            .CustomAttributes.Single(attribute => attribute.AttributeType.FullName == typeof(CensusProviderRoleAttribute).FullName);
        var metadata = new CustomAttribute(api.ImportReference(source.Constructor));
        foreach (var property in source.Properties) metadata.Properties.Add(property);
        assertion(metadata);
    }

    // Move a real compiled subclass and its real named Roles argument into the
    // actual production census, rather than inventing an unmatched attribute name.
    internal static void WithCompiledRole(ModuleDefinition api, Action<CustomAttribute> assertion)
        => WithCompiledAttribute(api, typeof(CensusProviderRoleAttribute), "Read", assertion);

    internal static void WithCompiledAnonymous(ModuleDefinition api, bool inherited, Action<CustomAttribute> assertion)
        => WithCompiledAttribute(api, inherited ? typeof(CensusInheritedAnonymousAttribute) : typeof(CensusAnonymousAttribute),
            inherited ? "InheritedAnonymous" : "Anonymous", assertion);

    private static void WithCompiledAttribute(ModuleDefinition api, Type attribute, string action, Action<CustomAttribute> assertion)
    {
        using var probes = ModuleDefinition.ReadModule(typeof(AuthenticationMetadata).Assembly.Location);
        var definition = probes.GetType(attribute.FullName);
        var source = probes.GetType(typeof(CensusAuthorizationProbe).FullName).Methods.Single(method => method.Name == action)
            .CustomAttributes.Single(attribute => attribute.AttributeType.FullName == definition.FullName);
        var properties = source.Properties.ToArray();
        _ = definition.CustomAttributes.Count;
        _ = definition.Interfaces.Count;
        foreach (var method in definition.Methods)
        {
            _ = method.CustomAttributes.Count;
            if (method.HasBody) _ = method.Body.Instructions.Count;
        }
        probes.Types.Remove(definition);
        api.Types.Add(definition);
        var metadata = new CustomAttribute(definition.Methods.Single(method => method.IsConstructor));
        foreach (var property in properties) metadata.Properties.Add(property);
        try { assertion(metadata); }
        finally { api.Types.Remove(definition); }
    }
}

internal sealed class CensusProviderRoleAttribute : AuthorizeAttribute;
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
internal sealed class CensusAnonymousAttribute : Attribute, IAllowAnonymous;
internal interface ICensusAnonymous : IAllowAnonymous;
internal abstract class CensusAnonymousBaseAttribute : Attribute, ICensusAnonymous;
internal sealed class CensusInheritedAnonymousAttribute : CensusAnonymousBaseAttribute;

internal static class CensusAuthorizationProbe
{
    [CensusProviderRole(Roles = "tenant-admin")]
    internal static void Read() { }

    [CensusAnonymous]
    internal static void Anonymous() { }

    [CensusInheritedAnonymous]
    internal static void InheritedAnonymous() { }
}
