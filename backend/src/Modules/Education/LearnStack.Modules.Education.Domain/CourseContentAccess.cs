namespace LearnStack.Modules.Education.Domain;

/// <summary>Course-level content policy inherited by lessons (ADR-0050).</summary>
public enum CourseContentAccess
{
    EnrollmentRequired = 0,
    Public = 1,
}
