using Mono.Cecil;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// IL reading for the rules whose question NetArchTest cannot answer.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why not NetArchTest here.</b> <c>Types.InAssembly</c> drops every type whose full name
/// begins with <c>System</c>, <c>Microsoft</c>, <c>xunit</c> or <c>netstandard</c> — even when
/// the assembly under test declares it. That is the right default for a scan of *someone
/// else's* dependencies and the wrong one for a rule about what an assembly of ours
/// contains: an extension class written in <c>namespace Microsoft.EntityFrameworkCore</c>, the
/// idiomatic place for one, would never reach the rule. The meta test
/// <c>Meta_NetArchTest_DetectsAPlantedViolation</c> makes that blindness loud for the rules
/// that still use NetArchTest; this reads the IL directly for the one whose subject is EF
/// Core itself.
/// </para>
/// </remarks>
internal static class Il
{
    /// <summary>
    /// Whether a type names a namespace anywhere the IL can carry it: its base type and
    /// interfaces, its attributes, its members' signatures, and its method bodies.
    /// </summary>
    public static bool NamesNamespace(TypeDefinition type, string namespacePrefix) =>
        ReferencedTypes(type).Any(reference => InNamespace(reference, namespacePrefix));

    /// <summary>
    /// The methods of a type whose signature or body names another type.
    /// </summary>
    /// <remarks>
    /// Type-level answers are too coarse for a context: a <c>DbContext</c> is allowed to map a
    /// guarded entity and is not allowed to query it, and both are the same type reference.
    /// </remarks>
    public static List<string> MethodsNaming(TypeDefinition type, string namedTypeFullName) =>
        [.. type.Methods
            .Where(method => MethodReferences(method).Any(reference =>
                NameOf(reference) == namedTypeFullName))
            .Select(method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static IEnumerable<TypeReference> MethodReferences(MethodDefinition method)
    {
        yield return method.ReturnType;

        foreach (var parameter in method.Parameters)
        {
            yield return parameter.ParameterType;
        }

        if (!method.HasBody)
        {
            yield break;
        }

        foreach (var variable in method.Body.Variables)
        {
            yield return variable.VariableType;
        }

        foreach (var instruction in method.Body.Instructions)
        {
            switch (instruction.Operand)
            {
                case TypeReference operand:
                    yield return operand;
                    break;
                case MethodReference call:
                    yield return call.ReturnType;
                    foreach (var parameter in call.Parameters)
                    {
                        yield return parameter.ParameterType;
                    }

                    if (call.DeclaringType is { } declaring)
                    {
                        yield return declaring;
                    }

                    break;
                case MemberReference member when member.DeclaringType is { } owner:
                    yield return owner;
                    break;
            }
        }
    }

    /// <summary>A reference's own name, or the name of the type it is built over.</summary>
    private static string NameOf(TypeReference reference) =>
        reference is GenericInstanceType generic
            ? generic.GenericArguments.Select(NameOf).FirstOrDefault(name => name.Length > 0) ?? generic.ElementType.FullName
            : reference.GetElementType().FullName;

    /// <summary>Every type reference one type carries.</summary>
    private static IEnumerable<TypeReference> ReferencedTypes(TypeDefinition type)
    {
        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (var contract in type.Interfaces)
        {
            yield return contract.InterfaceType;
        }

        foreach (var attribute in Attributes(type))
        {
            yield return attribute;
        }

        foreach (var field in type.Fields)
        {
            yield return field.FieldType;
        }

        foreach (var property in type.Properties)
        {
            yield return property.PropertyType;
        }

        foreach (var method in type.Methods)
        {
            yield return method.ReturnType;

            foreach (var parameter in method.Parameters)
            {
                yield return parameter.ParameterType;
            }

            if (!method.HasBody)
            {
                continue;
            }

            foreach (var variable in method.Body.Variables)
            {
                yield return variable.VariableType;
            }

            foreach (var instruction in method.Body.Instructions)
            {
                switch (instruction.Operand)
                {
                    case TypeReference operand:
                        yield return operand;
                        break;
                    case MemberReference member when member.DeclaringType is { } declaring:
                        yield return declaring;
                        break;
                }
            }
        }
    }

    private static IEnumerable<TypeReference> Attributes(TypeDefinition type) =>
        type.CustomAttributes.Select(attribute => attribute.AttributeType)
            .Concat(type.Fields.SelectMany(field => field.CustomAttributes)
                .Concat(type.Properties.SelectMany(property => property.CustomAttributes))
                .Concat(type.Methods.SelectMany(method => method.CustomAttributes))
                .Select(attribute => attribute.AttributeType));

    /// <summary>
    /// Whether a reference, or any type argument inside it, lives under a namespace.
    /// </summary>
    private static bool InNamespace(TypeReference reference, string namespacePrefix)
    {
        if (reference is GenericInstanceType generic
            && generic.GenericArguments.Any(argument => InNamespace(argument, namespacePrefix)))
        {
            return true;
        }

        if (reference.IsNested && reference.DeclaringType is { } declaring)
        {
            return InNamespace(declaring, namespacePrefix);
        }

        var element = reference.GetElementType();

        return element.Namespace.StartsWith(namespacePrefix, StringComparison.Ordinal);
    }
}
