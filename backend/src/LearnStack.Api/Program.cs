using LearnStack.Api.Composition;
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

builder.Services.AddOpenApi();
builder.Services.AddControllers();

var app = builder.Build();

app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

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
