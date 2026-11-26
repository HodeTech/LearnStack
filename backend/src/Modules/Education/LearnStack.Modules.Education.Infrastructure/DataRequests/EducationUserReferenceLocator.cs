using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.DataProtection;

namespace LearnStack.Modules.Education.Infrastructure.DataRequests;

/// <summary>
/// Where the Education module's audited aggregates hold a person reference in their audit snapshots: the creator,
/// updater and deleter of a course or a lesson
/// (<see href="../../../../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 § 7</see>).
/// </summary>
public sealed class EducationUserReferenceLocator : IUserReferenceLocator
{
    public IReadOnlyCollection<UserReferenceDeclaration> Redactable { get; } =
    [
        .. UserReferenceDeclarations.Attribution(typeof(Course), typeof(Lesson)),
    ];

    public IReadOnlyCollection<RetainedUserReference> Retained { get; } = [];
}
