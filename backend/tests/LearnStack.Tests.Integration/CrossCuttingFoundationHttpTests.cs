using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Results;
using FluentValidation;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// HTTP-level integration coverage for ADR-0032's L1 boundary +
/// ValidationBehavior. Backs the Standards 21 catalogue rows
/// <c>IExceptionHandler_Registered_AtStartup</c> and
/// <c>ValidationBehavior_DoesNotThrow_ValidationException</c> with the
/// end-to-end shape ASP.NET produces — Problem Details body, status
/// mapping, correlationId extension, content type.
/// </summary>
public sealed class CrossCuttingFoundationHttpTests(CrossCuttingHttpFixture fixture)
    : IClassFixture<CrossCuttingHttpFixture>
{
    private readonly HttpClient _client = fixture.CreateClient();

    [Fact]
    public async Task L1_Handler_Returns_ProblemDetails_For_Server_Side_ProviderException()
    {
        var response = await _client.GetAsync(new Uri("/test/throw-provider-5xx", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("dependency_unavailable");
        problem.GetProperty("messageKey").GetString().Should().Be("lockey_dependency_unavailable");
        problem.GetProperty("instance").GetString().Should().Be("/test/throw-provider-5xx");
        problem.TryGetProperty("correlationId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task L1_Handler_Returns_400_For_Client_Side_ProviderException()
    {
        var response = await _client.GetAsync(new Uri("/test/throw-provider-4xx", UriKind.Relative));

        // ProviderException(IsClientError: true) must map to 400 — the
        // production bug the review caught: bypassing HttpStatusMap.For(Exception)
        // mapped it to 503 via the LearnStackException default Error.Code.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ValidationBehavior_Returns_400_ProblemDetails_For_Invalid_Command()
    {
        // ADR-0032 § Sub-decision 3 — invalid input never reaches the
        // handler; ValidationBehavior returns Result.Fail(validation_failed)
        // and the controller's ToActionResult() projects to a 400
        // Problem Details body.
        var response = await _client.PostAsJsonAsync(
            new Uri("/test/validate", UriKind.Relative),
            new { Name = string.Empty });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("validation_failed");
        problem.GetProperty("messageKey").GetString().Should().Be("lockey_validation_failed");
        problem.GetProperty("instance").GetString().Should().Be("/test/validate");
        problem.GetProperty("errors").GetProperty("name")[0]
            .GetProperty("key").GetString().Should().Be("lockey_name_required");
    }

    [Fact]
    public async Task ValidationBehavior_Passes_Through_When_Command_Is_Valid()
    {
        var response = await _client.PostAsJsonAsync(
            new Uri("/test/validate", UriKind.Relative),
            new { Name = "alice" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("alice");
    }
}

/// <summary>
/// Shared <see cref="WebApplicationFactory{TEntryPoint}"/> that wires the
/// integration test's controllers + MediatR handler + validator into the
/// real <c>LearnStack.Api</c> host. Reuses the host's
/// <c>AddLearnStackCrossCuttingFoundation</c> wiring so the L1 handler +
/// ValidationBehavior tested here is the same code production runs.
/// </summary>
public sealed class CrossCuttingHttpFixture : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(Environments.Development);
        builder.ConfigureTestServices(services =>
        {
            services
                .AddControllers()
                .AddApplicationPart(typeof(CrossCuttingTestController).Assembly);

            // ValidationBehavior resolves IValidator<TRequest> from DI. We
            // register the validator + the handler so the test exercises
            // the full pipeline without re-running AddMediatR (which would
            // double-register the behaviors).
            services.AddTransient<
                IRequestHandler<TestValidationCommand, Result<string>>,
                TestValidationHandler>();
            services.AddTransient<IValidator<TestValidationCommand>, TestValidationValidator>();

            // TenantContextBehavior short-circuits when ITenantContext is
            // not resolved. Until Packet 7 lands TenantResolverMiddleware,
            // production has no way to flip IsResolved → true. For the
            // integration test we replace the scoped registration with a
            // fixed test tenant so MediatR's pipeline reaches the inner
            // handler.
            services.RemoveAll<ITenantContext>();
            services.AddScoped<ITenantContext>(_ => TestResolvedTenantContext.Instance);
        });
    }
}

internal sealed class TestResolvedTenantContext : ITenantContext
{
    public static TestResolvedTenantContext Instance { get; } = new();

    public bool IsResolved => true;
    public Guid TenantId { get; } = Guid.Parse("018f4d40-0000-7000-8000-000000000001");
    public Guid? OrganizationId { get; }
    public UserId? UserId { get; }
    public string? CorrelationId => null;
    public string? ModuleName => "integration-test";
}

[Route("test")]
public sealed class CrossCuttingTestController(IMediator mediator) : ControllerBase
{
    [HttpGet("throw-provider-5xx")]
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Controller actions are instance methods by ASP.NET routing convention.")]
    public IActionResult ThrowServerProvider() =>
        throw new ProviderException("test-provider", "upstream returned 5xx", isClientError: false);

    [HttpGet("throw-provider-4xx")]
    [SuppressMessage("Performance", "CA1822:Mark members as static",
        Justification = "Controller actions are instance methods by ASP.NET routing convention.")]
    public IActionResult ThrowClientProvider() =>
        throw new ProviderException("test-provider", "upstream returned 4xx", isClientError: true);

    [HttpPost("validate")]
    public async Task<IActionResult> Validate(
        [FromBody] TestValidationCommand command,
        CancellationToken cancellationToken)
    {
        var result = await mediator.Send(command, cancellationToken);
        return result.ToActionResult();
    }
}

public sealed record TestValidationCommand(string Name) : IRequest<Result<string>>;

internal sealed class TestValidationValidator : AbstractValidator<TestValidationCommand>
{
    public TestValidationValidator()
    {
        RuleFor(c => c.Name)
            .NotEmpty()
            .WithErrorCode("lockey_name_required");
    }
}

internal sealed class TestValidationHandler : IRequestHandler<TestValidationCommand, Result<string>>
{
    public Task<Result<string>> Handle(
        TestValidationCommand request, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Ok(request.Name));
}
