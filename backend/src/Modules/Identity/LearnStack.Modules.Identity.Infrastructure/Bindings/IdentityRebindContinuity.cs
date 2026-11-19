using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;

namespace LearnStack.Modules.Identity.Infrastructure.Bindings;

/// <summary>One stored user as the continuity proof reads it: only the four columns the rebind role may see.</summary>
public sealed record IdentityRebindStoredUser(IdentitySubjectKey SubjectKey, string? ProviderSubject, UserStatus Status);

/// <summary>What the continuity proof counted. Counts and classes only; no subject, key or person is named.</summary>
/// <param name="StoredActive">Active users of the stored authority.</param>
/// <param name="StoredClosing">Closing users, which neither rule below judges unless only their key is retained.</param>
/// <param name="StoredClosed">Closed tombstones, which retain only a key.</param>
/// <param name="Enumerated">Distinct subjects the new realm reported.</param>
/// <param name="Unkeyable">Reported subjects that cannot be a LearnStack subject (blank, over 255 characters or not storable text).</param>
/// <param name="MissingActive">Active users whose exact subject the new realm does not report.</param>
/// <param name="ReappearingClosed">Retained keys of Closed (or key-only Closing) users that a reported subject would claim again.</param>
/// <param name="Unbootstrapped">Reported subjects that match no stored user, which is a person who has not signed in yet.</param>
public sealed record IdentityRebindContinuityResult(
    int StoredActive,
    int StoredClosing,
    int StoredClosed,
    int Enumerated,
    int Unkeyable,
    int MissingActive,
    int ReappearingClosed,
    int Unbootstrapped)
{
    /// <summary>Whether continuity holds: no Active user is lost and no closed person can reappear.</summary>
    public bool Holds => MissingActive == 0 && ReappearingClosed == 0;
}

/// <summary>
/// The continuity proof of a controlled issuer rebind (ADR-0104 § 6) as a pure function of what the database
/// retains and what the new realm reports.
/// </summary>
/// <remarks>
/// <para>
/// Active rows retain the exact subject, so presence is a direct ordinal comparison. Closed rows retain only the
/// key, so absence needs the canonical encoding: the key of no reported subject may equal a retained Closed key,
/// which is what stops a tombstoned person from reappearing under the new issuer. A reported subject with no
/// stored key is allowed, because that person has not bootstrapped.
/// </para>
/// <para>
/// A Closing user is judged by neither rule while it retains its subject, because closure owns that transition; a
/// Closing user that retains only its key is judged like a Closed one. A reported subject that cannot be a
/// LearnStack subject is counted and cannot equal any stored one, since every stored subject was keyed under the
/// same rules.
/// </para>
/// </remarks>
public static class IdentityRebindContinuity
{
    /// <summary>Compares the stored users of <paramref name="authority"/> with the complete set the new realm reported.</summary>
    public static IdentityRebindContinuityResult Evaluate(
        IdentityAuthorityId authority,
        IReadOnlyCollection<IdentityRebindStoredUser> stored,
        IReadOnlyCollection<string> reported)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(reported);

        var subjects = new HashSet<string>(reported, StringComparer.Ordinal);
        var reportedKeys = new HashSet<string>(StringComparer.Ordinal);
        var unkeyable = 0;
        foreach (var subject in subjects)
        {
            try
            {
                reportedKeys.Add(IdentitySubjectKey.Compute(authority, subject).Hex);
            }
            catch (ArgumentException)
            {
                unkeyable++;
            }
        }

        int active = 0, closing = 0, closed = 0, missing = 0, reappearing = 0;
        var storedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var user in stored)
        {
            storedKeys.Add(user.SubjectKey.Hex);
            switch (user.Status)
            {
                case UserStatus.Active:
                    active++;
                    if (user.ProviderSubject is null || !subjects.Contains(user.ProviderSubject))
                    {
                        missing++;
                    }

                    break;
                case UserStatus.Closing:
                    closing++;
                    if (user.ProviderSubject is null && reportedKeys.Contains(user.SubjectKey.Hex))
                    {
                        reappearing++;
                    }

                    break;
                default:
                    closed++;
                    if (reportedKeys.Contains(user.SubjectKey.Hex))
                    {
                        reappearing++;
                    }

                    break;
            }
        }

        return new IdentityRebindContinuityResult(
            active, closing, closed, subjects.Count, unkeyable, missing, reappearing,
            reportedKeys.Count(key => !storedKeys.Contains(key)));
    }
}
