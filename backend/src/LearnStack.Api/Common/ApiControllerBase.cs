using Microsoft.AspNetCore.Mvc;

namespace LearnStack.Api.Common;

/// <summary>
/// Base type for every tenant-facing controller. Carries the two attributes
/// that would otherwise be repeated — and occasionally forgotten — on each
/// one: <see cref="ApiControllerAttribute"/> and the
/// <c>[controller]</c> route token that
/// <see cref="Versioning.VersionedRouteConvention"/> prefixes with
/// <c>api/v{N}</c>.
/// </summary>
/// <remarks>
/// <para>
/// The sanctioned action shape is the one
/// <see href="../../../../docs/decisions/0032-exception-handling-logging-and-observability.md">ADR-0032
/// § Sub-decision 6</see> fixes — explicit at every endpoint, no action filter
/// and no MediatR unwrap behavior:
/// </para>
/// <code>
/// [HttpPost]
/// public async Task&lt;IActionResult&gt; Create(CreateCourseCommand cmd, CancellationToken ct)
///     =&gt; (await _mediator.Send(cmd, ct)).ToActionResult();
/// </code>
/// <para>
/// <b>Derive from this type.</b> An earlier version of this comment said the
/// base class was "a convenience, not the enforcement point" and that a
/// controller deriving from <see cref="ControllerBase"/> directly was still
/// routed correctly. Both halves were wrong, and the second was actively
/// harmful: such a controller has no controller-level route template, so MVC
/// routes every action on it at the bare <c>api/v{N}</c> with the resource
/// segment dropped, and two of them collide as a 500
/// <c>AmbiguousMatchException</c> at request time. It also lacks
/// <see cref="ApiControllerAttribute"/>, so a malformed body bypasses the
/// automatic 400 and surfaces as a 500 rather than the single Problem Details
/// shape Standards 09 § API Surface fixes.
/// </para>
/// <para>
/// <see cref="Versioning.VersionedRouteConvention"/> now refuses to start
/// against a controller missing either, so the requirement is a startup
/// failure rather than a convention nobody is obliged to follow. Declaring
/// <c>[ApiController]</c> and a <c>[Route]</c> by hand satisfies it too; this
/// type is simply the one place that already does.
/// </para>
/// </remarks>
[ApiController]
[Route("[controller]")]
public abstract class ApiControllerBase : ControllerBase;
