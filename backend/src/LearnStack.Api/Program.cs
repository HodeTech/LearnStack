using LearnStack.Api.Common;
using LearnStack.Api.Composition;
using LearnStack.Api.Tenancy;
using LearnStack.Api.Versioning;
using LearnStack.SharedKernel.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Resolve the deployment mode once at the composition root. Modules never
// read DeploymentMode (architecture test
// Modules_Do_Not_Reference_DeploymentMode enforces it); the value selects
// the right error tracker, OTLP exporter target, and (later packets) the
// right Dapr / entitlement / host-resolver implementations per
// docs/standards/20-infrastructure-stack.md § Composition Root.
// No default. The key used to ship as "Development" in appsettings.json —
// the file that goes to every environment — while appsettings.Development.json
// set none, so every Development-guarded mechanism was on by default in a
// deployment that never configured it. A startup failure naming the key is the
// version of that mistake an operator can see (ADR-0036).
var deploymentMode = builder.Configuration.RequireDeploymentMode();

builder.AddLearnStackCrossCuttingFoundation(deploymentMode);
builder.Services.AddLearnStackTenancyEdge(builder.Configuration, deploymentMode);

// Controllers + the /api/v{N} route convention + one OpenAPI document per
// live major, per ADR-0024. AddLearnStackApiVersioning owns the
// AddControllers call so the convention cannot be registered twice or, worse,
// registered after a second AddControllers has already built the model.
builder.Services.AddLearnStackApiVersioning();

var app = builder.Build();

app.UseExceptionHandler();

// Outermost after the exception handler, so every response carries the
// correlation id — including the ones produced by the middleware below.
app.UseLearnStackCorrelationHeader();

// 404 and 405 come from routing, before MVC — no action runs, so no MVC hook
// sees them and they reach the client with no body. Registered after the
// exception handler so an error that already produced a body keeps it.
app.MapLearnStackClientErrors();

// The OpenAPI document and its reference UI are served in every environment,
// not only Development. The document IS the contract Standards 04 § OpenAPI
// publishes and the SDK generates from; hiding it outside Development would
// mean the artefact CI diffs is one no deployed instance serves. Exposure is
// an edge concern — APISIX blocks or allow-lists /openapi per environment.
app.MapLearnStackOpenApi();

// X-Tenant-Id / X-Organization-Id are assertions: compared against what the
// API resolved, never a source of it (ADR-0036). Registered after
// MapLearnStackClientErrors so a rejection gets the one Problem Details shape,
// and before the endpoints so no handler runs on a request that lost the
// comparison.
app.UseLearnStackTenantAssertions();

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))
    .WithName("HealthCheck");

app.MapControllers();

app.Run();

// `public partial class Program` is the top-level-statements escape hatch
// that lets WebApplicationFactory<Program> in the test assemblies resolve
// the entry-point type. CA1515 is downgraded to `none` for this project
// in `backend/src/LearnStack.Api/.editorconfig` — the test harness is the
// external consumer and it cannot see `internal` types without an
// InternalsVisibleTo dance that confuses Program-discovery.
public partial class Program;
