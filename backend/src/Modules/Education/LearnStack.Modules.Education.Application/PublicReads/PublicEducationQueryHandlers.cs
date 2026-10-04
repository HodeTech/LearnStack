using LearnStack.Modules.Education.Application.Abstractions;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Modules.Education.Application.PublicReads;

public sealed class GetPublicCoursesQueryHandler(
    IPublicTenantConfigurationReader configuration, IPublicEducationReadStore store, ITenantContext context, PublicEducationDisplay display)
    : IRequestHandler<GetPublicCoursesQuery, Result<PublicCourseCatalog>>
{
    public async Task<Result<PublicCourseCatalog>> Handle(GetPublicCoursesQuery request, CancellationToken cancellationToken)
    {
        var configured = await PublicEducationFailures.ConfigurationAsync(request.Input, configuration, cancellationToken);
        if (configured.IsFailure) return Result<PublicCourseCatalog>.Fail(configured.Error!);
        var locale = configured.Value;
        var scope = PublicReadValidation.Scope(context, request.Input, locale.Locale, false)
            ?? throw new InvalidOperationException("The pipeline must establish public host provenance.");
        CatalogContinuation? after = null;
        if (request.Input.Cursor is { } raw && !PublicCursorCodec.TryCatalog(raw, scope, out after))
            return PublicEducationFailures.Invalid<PublicCourseCatalog>("cursor");
        PublicReadValidation.TryLimit(request.Input.Limit, out var limit);
        var fetched = await store.ReadCatalogAsync(locale.Locale, after, limit + 1, cancellationToken);
        var rows = fetched.Take(limit).ToArray();
        var items = await display.SummariesAsync(rows, locale, cancellationToken);
        var next = fetched.Count > limit && rows.Length != 0
            ? PublicCursorCodec.EncodeCatalog(scope, new CatalogContinuation(rows[^1].CreatedAt, rows[^1].Id)) : null;
        return Result.Ok(new PublicCourseCatalog(locale.Locale, items, new PageInfo(next, null, next is not null, false)));
    }
}

public sealed class GetPublicCourseQueryHandler(
    IPublicTenantConfigurationReader configuration, IPublicEducationReadStore store, ITenantContext context, PublicEducationDisplay display)
    : IRequestHandler<GetPublicCourseQuery, Result<PublicCourseDetail>>
{
    public async Task<Result<PublicCourseDetail>> Handle(GetPublicCourseQuery request, CancellationToken cancellationToken)
    {
        var configured = await PublicEducationFailures.ConfigurationAsync(request.Input, configuration, cancellationToken);
        if (configured.IsFailure) return Result<PublicCourseDetail>.Fail(configured.Error!);
        var locale = configured.Value;
        var row = await store.ReadCourseAsync(request.Slug, locale.Locale, cancellationToken);
        if (row is null) return PublicEducationFailures.Hidden<PublicCourseDetail>();
        var scope = PublicReadValidation.Scope(context, request.Input, locale.Locale, true)
            ?? throw new InvalidOperationException("The pipeline must establish public host provenance.");
        OutlineContinuation? after = null;
        if (request.Input.Cursor is { } raw
            && (!PublicCursorCodec.TryOutline(raw, scope, out after) || after!.Parent != row.Id))
            return PublicEducationFailures.Invalid<PublicCourseDetail>("lessonCursor");
        var summary = (await display.SummariesAsync([row], locale, cancellationToken)).Single();
        var alternates = (await store.ReadCourseAlternatesAsync(row.Id, locale.Locale, locale.EnabledLocales, cancellationToken))
            .OrderBy(item => Array.IndexOf(locale.EnabledLocales, item.Locale)).ThenBy(item => item.Locale, StringComparer.Ordinal).ToArray();
        PublicCourseOutline? outline = null;
        if (row.ContentAccess == CourseContentAccess.Public)
        {
            PublicReadValidation.TryLimit(request.Input.Limit, out var limit);
            var fetched = await store.ReadOutlineAsync(row.Id, locale.Locale, after, limit + 1, cancellationToken);
            var rows = fetched.Take(limit).ToArray();
            var next = fetched.Count > limit && rows.Length != 0
                ? PublicCursorCodec.EncodeOutline(scope, new OutlineContinuation(row.Id, rows[^1].Sort, rows[^1].Id)) : null;
            outline = new PublicCourseOutline(rows.Select(item => new PublicOutlineItem(item.Slug, item.Title, item.Sort)).ToArray(),
                new PageInfo(next, null, next is not null, false));
        }
        return Result.Ok(new PublicCourseDetail(locale.Locale, summary, alternates, outline));
    }
}

public sealed class GetPublicLessonQueryHandler(
    IPublicTenantConfigurationReader configuration, IPublicEducationReadStore store, PublicEducationDisplay display)
    : IRequestHandler<GetPublicLessonQuery, Result<PublicLessonDetail>>
{
    public async Task<Result<PublicLessonDetail>> Handle(GetPublicLessonQuery request, CancellationToken cancellationToken)
    {
        var configured = await PublicEducationFailures.ConfigurationAsync(request.Input, configuration, cancellationToken);
        if (configured.IsFailure) return Result<PublicLessonDetail>.Fail(configured.Error!);
        var locale = configured.Value;
        var row = await store.ReadLessonAsync(request.Slug, request.LessonSlug, locale.Locale, cancellationToken);
        if (row is null) return PublicEducationFailures.Hidden<PublicLessonDetail>();
        var content = await display.ContentAsync(row, locale, cancellationToken);
        var alternates = (await store.ReadLessonAlternatesAsync(row.CourseId, row.Id, locale.Locale, locale.EnabledLocales, cancellationToken))
            .OrderBy(item => Array.IndexOf(locale.EnabledLocales, item.Locale)).ThenBy(item => item.Locale, StringComparer.Ordinal).ToArray();
        return Result.Ok(new PublicLessonDetail(locale.Locale, new PublicNamedResource(row.CourseSlug, row.CourseTitle),
            new PublicNamedResource(row.Slug, row.Title), alternates, content));
    }
}
