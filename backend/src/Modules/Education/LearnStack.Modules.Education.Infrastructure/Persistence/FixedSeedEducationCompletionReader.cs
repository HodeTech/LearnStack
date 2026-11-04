using System.Text.Json;
using System.Text.Json.Nodes;
using LearnStack.Modules.Education.Application.Contracts.Seeding;
using LearnStack.Modules.Education.Domain;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace LearnStack.Modules.Education.Infrastructure.Persistence;

/// <summary>Checks the fixed complete curriculum across branches without exporting its content.</summary>
public sealed class FixedSeedEducationCompletionReader(EducationDbContext db, IUnitOfWork unit)
    : ISeedEducationCompletionReader
{
    public async Task<bool> VerifyAsync(SeedCompletionReadWitness witness, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(witness);
        RequireCurrent(witness);
        var declaration = witness.GetDeclaration<SeedEducationDeclaration>();
        if (declaration.TenantId != witness.TenantId || declaration.Courses.IsDefaultOrEmpty
            || declaration.Courses.Select(course => course.Id).Distinct().Count() != declaration.Courses.Length
            || declaration.Courses.SelectMany(course => course.Lessons).Select(lesson => lesson.Id).Distinct().Count()
                != declaration.Courses.Sum(course => course.Lessons.Length)) return false;

        var previousTimeout = db.Database.GetCommandTimeout();
        db.Database.SetCommandTimeout(10);
        try
        {
            foreach (var expected in declaration.Courses)
            {
                if (expected.Id == Guid.Empty) return false;
                var courseId = CourseId.From(expected.Id);
                var course = await db.Courses.AsNoTracking().Include(row => row.Translations)
                    .SingleOrDefaultAsync(row => row.Id == courseId, cancellationToken);
                RequireCurrent(witness);
                if (course is null || !CourseMatches(course, expected, declaration.TenantId)) return false;
                foreach (var expectedLesson in expected.Lessons)
                {
                    if (expectedLesson.Id == Guid.Empty) return false;
                    var lessonId = LessonId.From(expectedLesson.Id);
                    var lesson = await db.Lessons.AsNoTracking().Include(row => row.Translations)
                        .SingleOrDefaultAsync(row => row.Id == lessonId, cancellationToken);
                    RequireCurrent(witness);
                    if (lesson is null || !LessonMatches(lesson, expected, expectedLesson, declaration.TenantId)) return false;
                }
            }
            return true;
        }
        finally { db.Database.SetCommandTimeout(previousTimeout); }
    }

    private void RequireCurrent(SeedCompletionReadWitness witness)
    {
        witness.RequireCurrent();
        if (!unit.HasActiveTransaction || unit.IsRollbackOnly || unit.Mode != TransactionMode.ReadWrite
            || unit is not IOperationReadScopeView { IsTenantWideRead: true }
            || unit is IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
            || unit.Transaction is not { } transaction
            || unit.GetTenantContextAnnouncement(transaction) is not { Generation: > 0 } announcement
            || announcement.TenantId != witness.TenantId || announcement.OrganizationId is not null
            || witness.OrganizationId is not null || !witness.IsBoundTo(unit, transaction, announcement.Generation)
            || db.CurrentTenantId != witness.TenantId || db.CurrentOrganizationId is not null || !db.CurrentTenantWideRead
            || !ReferenceEquals(db.Database.GetDbConnection(), unit.Connection)
            || !ReferenceEquals(db.Database.CurrentTransaction?.GetDbTransaction(), transaction)
            || db.ChangeTracker.Entries().Any(entry => entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
            throw new InvalidOperationException("Fixed education completion requires its active verification owner.");
    }

    private static bool CourseMatches(Course row, SeedCourseDeclaration expected, TenantId tenant)
    {
        var published = expected.Status == nameof(PublicationStatus.Published);
        return expected.Status is nameof(PublicationStatus.Draft) or nameof(PublicationStatus.Published)
            && row.Id.Value == expected.Id && row.TenantId == tenant && row.OrganizationId == expected.OrganizationId
            && row.SlugKey == expected.SlugKey
            && row.Status.ToString() == expected.Status
            && (row.ContentAccess == CourseContentAccess.Public ? "public" : "enrollment_required") == expected.ContentAccess
            && row.LevelTaxonomyKey == expected.LevelTaxonomyKey && row.LevelTaxonomySchemaVersion == expected.LevelTaxonomySchemaVersion
            && row.LevelBandKey == expected.LevelBandKey && Pristine(row, expected.Translations.Length + (published ? 1 : 0))
            && row.Translations.Count == expected.Translations.Length
            && row.Translations.All(actual => actual.TenantId == tenant && actual.OrganizationId == expected.OrganizationId
                && actual.CourseId == row.Id && expected.Translations.Count(pin => pin.Locale == actual.Locale
                    && pin.Title == actual.Title && pin.Summary == actual.Summary && pin.Slug == actual.Slug) == 1);
    }

    private static bool LessonMatches(Lesson row, SeedCourseDeclaration parent, SeedLessonDeclaration expected, TenantId tenant)
    {
        var published = expected.Status == nameof(PublicationStatus.Published);
        return expected.Status is nameof(PublicationStatus.Draft) or nameof(PublicationStatus.Published)
            && row.Id.Value == expected.Id && row.TenantId == tenant && row.OrganizationId == parent.OrganizationId
            && row.CourseId.Value == parent.Id
            && row.Sort == expected.Sort && row.Status.ToString() == expected.Status
            && row.ContentTypeKey == expected.ContentTypeKey && row.ContentTypeSchemaVersion == expected.ContentTypeSchemaVersion
            && Pristine(row, expected.Translations.Length + (published ? 1 : 0))
            && row.Translations.Count == expected.Translations.Length
            && row.Translations.All(actual => actual.TenantId == tenant && actual.OrganizationId == parent.OrganizationId
                && actual.LessonId == row.Id && expected.Translations.Count(pin => pin.Locale == actual.Locale
                    && pin.Title == actual.Title && pin.Slug == actual.Slug && Json(pin.Body, actual.Body)) == 1);
    }

    private static bool Pristine<TId>(AuditableEntity<TId> row, long version)
        where TId : struct, IStronglyTypedId<Guid>, IEquatable<TId> => row.Version == version
        && row.CreatedAt != default && row.CreatedBy == UserId.SystemActor && row.DeletedAt is null && row.DeletedBy is null
        && (version == 0 ? row.UpdatedAt is null && row.UpdatedBy is null
            : row.UpdatedAt is { } updated && updated != default && row.UpdatedBy == UserId.SystemActor);

    private static bool Json(string left, string right)
    {
        try { return JsonNode.DeepEquals(JsonNode.Parse(left), JsonNode.Parse(right)); }
        catch (JsonException) { return false; }
    }
}
