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
    /// <remarks>
    /// A compiler-generated nested type counts as part of the type that declares it: an async
    /// method's body, and a lambda's, live there rather than in the method the author wrote.
    /// </remarks>
    public static bool NamesNamespace(TypeDefinition type, string namespacePrefix) =>
        WithGenerated(type).SelectMany(ReferencedTypes)
            .Any(reference => InNamespace(reference, namespacePrefix));

    /// <summary>A type and the compiler-generated types nested inside it, recursively.</summary>
    private static IEnumerable<TypeDefinition> WithGenerated(TypeDefinition type)
    {
        yield return type;

        foreach (var nested in type.NestedTypes.Where(IsGenerated).SelectMany(WithGenerated))
        {
            yield return nested;
        }
    }

    /// <summary>
    /// The methods of a type whose signature or body names another type.
    /// </summary>
    /// <remarks>
    /// Type-level answers are too coarse for a context: a <c>DbContext</c> is allowed to map a
    /// guarded entity and is not allowed to query it, and both are the same type reference.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// A compiler-generated nested type is read as part of the method that declares it and
    /// reported under that method's name. An <c>async</c> method's real body — every call,
    /// local and constructed object in it — is emitted into a nested state machine, so a walk
    /// over the outer method alone sees boilerplate: measured, an <c>async</c> helper on a
    /// <c>DbContext</c> that read the guarded entity was invisible while its synchronous twin
    /// was caught.
    /// </para>
    /// </remarks>
    public static List<string> MethodsNaming(TypeDefinition type, string namedTypeFullName) =>
        [.. Methods(type)
            .Where(method => MethodReferences(method.Definition).Any(reference =>
                NamesOf(reference).Contains(namedTypeFullName, StringComparer.Ordinal)))
            .Select(method => method.DeclaredAs)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    /// <summary>
    /// Every method of a type, each paired with the name it was written under — a state
    /// machine's <c>MoveNext</c> is reported as the async method that declares it.
    /// </summary>
    public static IEnumerable<(MethodDefinition Definition, string DeclaredAs)> Methods(TypeDefinition type)
    {
        foreach (var method in type.Methods)
        {
            yield return (method, method.Name);
        }

        foreach (var nested in type.NestedTypes.Where(IsGenerated))
        {
            var declaredAs = WrittenAs(nested);

            foreach (var method in Methods(nested))
            {
                yield return (method.Definition, declaredAs);
            }
        }
    }

    /// <summary>The method a compiler-generated type was emitted for, or the type's own name.</summary>
    private static string WrittenAs(TypeDefinition generated)
    {
        var opening = generated.Name.IndexOf('<', StringComparison.Ordinal);
        var closing = generated.Name.IndexOf('>', StringComparison.Ordinal);

        return closing > opening + 1
            ? generated.Name[(opening + 1)..closing]
            : generated.Name;
    }

    private static bool IsGenerated(TypeDefinition type) =>
        type.Name.Contains('<', StringComparison.Ordinal)
        || type.CustomAttributes.Any(attribute =>
            attribute.AttributeType.Name == "CompilerGeneratedAttribute");

    private static IEnumerable<TypeReference> MethodReferences(MethodDefinition method)
    {
        yield return method.ReturnType;

        foreach (var parameter in method.Parameters)
        {
            yield return parameter.ParameterType;
        }

        foreach (var constraint in method.GenericParameters.SelectMany(parameter => parameter.Constraints))
        {
            yield return constraint.ConstraintType;
        }

        if (!method.HasBody)
        {
            yield break;
        }

        foreach (var variable in method.Body.Variables)
        {
            yield return variable.VariableType;
        }

        // The type a catch clause names is a reference like any other, and the only one a
        // walk over signatures and instructions does not reach.
        foreach (var handler in method.Body.ExceptionHandlers.Where(handler => handler.CatchType is not null))
        {
            yield return handler.CatchType;
        }

        foreach (var instruction in method.Body.Instructions)
        {
            switch (instruction.Operand)
            {
                case TypeReference operand:
                    yield return operand;
                    break;
                case GenericInstanceMethod generic:
                    // The call site's type arguments, which is where `Set<PlatformEntitlement>()`
                    // names the entity — the method's own return type is an open parameter.
                    foreach (var argument in generic.GenericArguments)
                    {
                        yield return argument;
                    }

                    yield return generic.ReturnType;

                    if (generic.DeclaringType is { } genericOwner)
                    {
                        yield return genericOwner;
                    }

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

    /// <summary>Every name a reference carries: its own, and each type it is built over.</summary>
    /// <remarks>
    /// All of them, not the first. A <c>DbSet&lt;Guarded&gt;</c> names the guarded type in its
    /// only argument, but a <c>Dictionary&lt;Guid, Guarded&gt;</c> names it in the second — and
    /// taking the first argument answered <c>System.Guid</c> and reported the member clean.
    /// The constructed type's own name is included too, so a member typed
    /// <c>Guarded&lt;T&gt;</c> is found by its element name.
    /// </remarks>
    private static IEnumerable<string> NamesOf(TypeReference reference)
    {
        if (reference is GenericInstanceType generic)
        {
            yield return generic.ElementType.GetElementType().FullName;

            foreach (var name in generic.GenericArguments.SelectMany(NamesOf))
            {
                yield return name;
            }

            yield break;
        }

        yield return reference.GetElementType().FullName;
    }

    /// <summary>Every type reference one type carries.</summary>
    private static IEnumerable<TypeReference> ReferencedTypes(TypeDefinition type)
    {
        if (type.BaseType is { } baseType)
        {
            yield return baseType;
        }

        foreach (var constraint in type.GenericParameters.SelectMany(parameter => parameter.Constraints))
        {
            yield return constraint.ConstraintType;
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

        // One walker, not two: this loop used to carry its own copy, and the copy saw less —
        // a called method's return and parameter types were invisible to it, so a namespace
        // named only there was never reported.
        foreach (var reference in type.Methods.SelectMany(MethodReferences))
        {
            yield return reference;
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
