using LearnStack.SharedKernel.Identifiers;

namespace LearnStack.Modules.Identity.Application.Abstractions;

/// <summary>
/// Resolves an operator-supplied provider subject to the Active User it names, under the stored
/// identity authority (ADR-0102 § 5).
/// </summary>
/// <remarks>
/// A confined single-act resolver: the request supplies only the provider subject, never the authority
/// or the derived key, and an unknown or non-Active subject answers null. The operator creates no
/// User and an email address is never an identifier.
/// </remarks>
public interface IOperatorSubjectResolver
{
    /// <summary>The Active user for <paramref name="providerSubject"/>, or null.</summary>
    Task<UserId?> ResolveActiveUserAsync(string providerSubject, CancellationToken cancellationToken = default);
}
