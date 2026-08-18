using LearnStack.Api.Composition;
using LearnStack.Api.Versioning;
using LearnStack.SharedKernel.Hosting;

var builder = WebApplication.CreateBuilder(args);

// Resolve the deployment mode once at the composition root. Modules never
// read DeploymentMode (architecture test
// Modules_Do_Not_Reference_DeploymentMode enforces it); the value selects
// the right error tracker, OTLP exporter target, and (later packets) the
// right Dapr / entitlement / host-resolver implementations per
// docs/standards/20-infrastructure-stack.md § Composition Root.
var deploymentMode = builder.Configuration.GetValue("Deployment:Mode", DeploymentMode.Development);

builder.AddLearnStackCrossCuttingFoundation(deploymentMode);

// Controllers + the /api/v{N} route convention + one OpenAPI document per
// live major, per ADR-0024. AddLearnStackApiVersioning owns the
// AddControllers call so the convention cannot be registered twice or, worse,
// registered after a second AddControllers has already built the model.
builder.Services.AddLearnStackApiVersioning();

var app = builder.Build();

app.UseExceptionHandler();

// The OpenAPI document and its reference UI are served in every environment,
// not only Development. The document IS the contract Standards 04 § OpenAPI
// publishes and the SDK generates from; hiding it outside Development would
// mean the artefact CI diffs is one no deployed instance serves. Exposure is
// an edge concern — APISIX blocks or allow-lists /openapi per environment.
app.MapLearnStackOpenApi();

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
