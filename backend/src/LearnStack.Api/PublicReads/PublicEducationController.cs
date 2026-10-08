using LearnStack.Api.Common;
using LearnStack.Modules.Education.Application.Contracts.PublicReads;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.PublicReads;

/// <summary>Institution marketing and eligible content; all authority stays in the pipeline.</summary>
[AllowAnonymous]
[Route("public/courses")]
public sealed class PublicEducationController(ISender sender) : ApiControllerBase
{
    [HttpGet(Name = "GetPublicCourses")]
    [ProducesResponseType<PublicCourseCatalog>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Catalog(CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicCoursesQuery(PublicReadTransport.Input(HttpContext, false, false)), cancellationToken)).ToActionResult();
    [HttpHead(Name = "HeadPublicCourses")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> HeadCatalog(CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicCoursesQuery(PublicReadTransport.Input(HttpContext, false, false)), cancellationToken)).ToActionResult();
    [HttpGet("{slug}", Name = "GetPublicCourse")]
    [ProducesResponseType<PublicCourseDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Course(string slug, CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicCourseQuery(slug, PublicReadTransport.Input(HttpContext, true, false)), cancellationToken)).ToActionResult();
    [HttpHead("{slug}", Name = "HeadPublicCourse")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> HeadCourse(string slug, CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicCourseQuery(slug, PublicReadTransport.Input(HttpContext, true, false)), cancellationToken)).ToActionResult();
    [HttpGet("{slug}/lessons/{lessonSlug}", Name = "GetPublicLesson")]
    [ProducesResponseType<PublicLessonDetail>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Lesson(string slug, string lessonSlug, CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicLessonQuery(slug, lessonSlug, PublicReadTransport.Input(HttpContext, false, true)), cancellationToken)).ToActionResult();
    [HttpHead("{slug}/lessons/{lessonSlug}", Name = "HeadPublicLesson")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> HeadLesson(string slug, string lessonSlug, CancellationToken cancellationToken) =>
        (await sender.Send(new GetPublicLessonQuery(slug, lessonSlug, PublicReadTransport.Input(HttpContext, false, true)), cancellationToken)).ToActionResult();
}
