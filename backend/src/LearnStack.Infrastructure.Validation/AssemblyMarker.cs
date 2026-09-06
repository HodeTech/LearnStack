namespace LearnStack.Infrastructure.Validation;

/// <summary>
/// Anchor type for assembly-level reflection — architecture tests, and any
/// registration that scans this assembly.
/// </summary>
/// <remarks>
/// Every sibling <c>LearnStack.Infrastructure.*</c> project carries one. A rule
/// that resolves an assembly by name rather than by a type is a rule that passes
/// vacuously when the name is wrong.
/// </remarks>
public static class AssemblyMarker;
