// TODO(2026-05-19, @platform, phase-02a): wire OpenTelemetry — traces +
// metrics + logs via AddOpenTelemetry(); the OpenTelemetry.* packages are
// already reserved in Directory.Packages.props. LearnStack.Tests.Contract
// should then assert /openapi/v1.json advertises the correlation-id header.
//
// TODO(2026-05-19, @platform, phase-02a): revisit appsettings.Development.json
// EF Core logging level — currently `Information` logs every SQL statement
// including parameter values. Once handlers land and parameters may carry PII,
// drop to `Warning` and route SQL traces through OpenTelemetry instead
// (Standards 11 § Logging Hygiene).

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.MapGet("/healthz", () => Results.Ok(new { status = "healthy" }))
    .WithName("HealthCheck");

app.Run();

// `public partial class Program` is the top-level-statements escape hatch
// that lets WebApplicationFactory<Program> in the test assemblies resolve
// the entry-point type. CA1515 is downgraded to `none` for this project
// in `backend/src/LearnStack.Api/.editorconfig` — the test harness is the
// external consumer and it cannot see `internal` types without an
// InternalsVisibleTo dance that confuses Program-discovery.
public partial class Program;
