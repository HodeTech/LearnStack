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
/// Deriving is a convenience, not the enforcement point. A controller that
/// derives from <see cref="ControllerBase"/> directly is still routed under
/// <c>/api/v{N}</c> by the convention, and
/// <c>Every_Endpoint_Is_Under_Versioned_Route</c> is what fails the build if it
/// somehow is not — a base class nobody is obliged to use cannot be a
/// guarantee.
/// </para>
/// </remarks>
[ApiController]
[Route("[controller]")]
public abstract class ApiControllerBase : ControllerBase;
