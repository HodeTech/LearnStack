using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Caching;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Caching;
using LearnStack.SharedKernel.Messaging;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
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
        var response = await _client.GetAsync(new Uri("/api/v1/test/throw-provider-5xx", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("dependency_unavailable");
        problem.GetProperty("messageKey").GetString().Should().Be("lockey_dependency_unavailable");
        problem.GetProperty("instance").GetString().Should().Be("/api/v1/test/throw-provider-5xx");
        problem.TryGetProperty("correlationId", out _).Should().BeTrue();
    }

    [Fact]
    public async Task L1_Handler_Returns_Consistent_Body_And_Status_For_Client_Side_ProviderException()
    {
        var response = await _client.GetAsync(new Uri("/api/v1/test/throw-provider-4xx", UriKind.Relative));

        // An adapter surfacing a provider 4xx as client-actionable passes an
        // explicit Error (here validation_failed). HTTP status is derived
        // from that code, so body code and status agree — 400 + validation_failed,
        // NOT a 400 carrying dependency_unavailable. IsClientError only gates
        // Sentry capture, not the status.
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("validation_failed");
        problem.GetProperty("messageKey").GetString().Should().Be("lockey_validation_failed");
    }

    [Fact]
    public async Task ValidationBehavior_Returns_400_ProblemDetails_For_Invalid_Command()
    {
        // ADR-0032 § Sub-decision 3 — invalid input never reaches the
        // handler; ValidationBehavior returns Result.Fail(validation_failed)
        // and the controller's ToActionResult() projects to a 400
        // Problem Details body.
        var response = await _client.PostAsJsonAsync(
            new Uri("/api/v1/test/validate", UriKind.Relative),
            new { Name = string.Empty });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("validation_failed");
        problem.GetProperty("messageKey").GetString().Should().Be("lockey_validation_failed");
        problem.GetProperty("instance").GetString().Should().Be("/api/v1/test/validate");
        problem.GetProperty("errors").GetProperty("name")[0]
            .GetProperty("key").GetString().Should().Be("lockey_name_required");
    }

    [Fact]
    public async Task ValidationBehavior_Passes_Through_When_Command_Is_Valid()
    {
        var response = await _client.PostAsJsonAsync(
            new Uri("/api/v1/test/validate", UriKind.Relative),
            new { Name = "alice" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("alice");
    }

    [Fact]
    public async Task Malformed_Body_Returns_LearnStacks_ProblemDetails_Not_AspNets()
    {
        // [ApiController]'s automatic 400 runs before MediatR, so this never
        // reaches ValidationBehavior. Left at its default it would emit
        // ASP.NET's own Problem Details — English framework text plus the
        // binder's parameter names — a second error shape Standards 09
        // § API Surface does not admit.
        var response = await _client.PostAsync(
            new Uri("/api/v1/test/validate", UriKind.Relative),
            new StringContent("{\"name\": 123}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType
            .Should().Be("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("code").GetString().Should().Be("validation_failed");
        problem.GetProperty("messageKey").GetString().Should().Be("lockey_validation_failed");

        // The binder's own message names an internal type ("could not be
        // converted to System.String"), so it is dropped. What survives is the
        // field path the client needs, in the same errors map
        // ValidationBehavior produces.
        problem.GetProperty("errors").EnumerateObject().Should().NotBeEmpty();
        (await response.Content.ReadAsStringAsync())
            .Should().NotContain("System.", "the binder's type names must not reach a client");
    }
}

/// <summary>
/// The foundation sockets resolve from the real composition root.
/// </summary>
/// <remarks>
/// A registration compiles whether or not it can be satisfied, so "it builds" is
/// not evidence that a caller can get one. These resolve through the host the
/// application actually starts, which is the only place the lifetimes and the
/// dependency graph are the real ones — <c>InProcessEventBus</c> takes an
/// <c>IServiceScopeFactory</c>, an <c>ITenantContextAccessor</c> and an
/// <c>IPartitionSerializer</c>, and a singleton depending on a scoped service is
/// a startup failure rather than a compile error.
/// </remarks>
public sealed class FoundationPortResolutionTests(CrossCuttingHttpFixture fixture)
    : IClassFixture<CrossCuttingHttpFixture>
{
    [Fact]
    public void The_Event_Bus_Resolves_To_The_In_Process_Transport()
    {
        using var scope = fixture.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<IEventBus>()
            .Should().BeOfType<InProcessEventBus>();
    }

    [Fact]
    public void The_Partition_Serializer_Is_A_Singleton()
    {
        // The ordering guarantee is process-wide. One instance per scope would
        // give each publisher its own chains, so two events on one partition key
        // would run concurrently — while every unit test still passed, because
        // each of those builds one serializer and uses it throughout.
        using var first = fixture.Services.CreateScope();
        using var second = fixture.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<IPartitionSerializer>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<IPartitionSerializer>());
    }

    [Fact]
    public void The_Cache_Resolves_To_The_In_Memory_Default()
    {
        using var scope = fixture.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<ICacheService>()
            .Should().BeOfType<InMemoryCacheService>();
    }

    [Fact]
    public void The_Process_Local_Cache_Is_A_Singleton_Across_Request_Scopes()
    {
        using var first = fixture.Services.CreateScope();
        using var second = fixture.Services.CreateScope();

        first.ServiceProvider.GetRequiredService<ICacheService>()
            .Should().BeSameAs(second.ServiceProvider.GetRequiredService<ICacheService>());
    }
}

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
            // The enforcement probes in VersionedRouteEnforcementTests are
            // deliberately broken; without this filter each one aborts this
            // host at startup. Index 0 so it runs before VersionedRouteConvention.
            services.AddControllers(options =>
                    options.Conventions.Insert(0, new TestControllerFilter(
                        typeof(CrossCuttingTestController))))
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

            // TransactionBehavior opens a real transaction on every request that
            // reaches step 6 — ADR-0040 § Decision has no gate, deliberately,
            // because a read needs the SET LOCAL as much as a write does. This
            // host has no database: it is a WebApplicationFactory in the non-Docker
            // CI job, and what it tests is validation and the Problem Details
            // shape. So the seam is replaced rather than satisfied. The real
            // protocol is asserted in TransactionBehaviorTests (the call order)
            // and UnitOfWorkTests (against a real PostgreSQL).
            services.RemoveAll<IUnitOfWork>();
            services.AddScoped<IUnitOfWork, NoDatabaseUnitOfWork>();
        });
    }
}

/// <summary>
/// An <see cref="IUnitOfWork"/> for a host with no database.
/// </summary>
/// <remarks>
/// Every member is a no-op except <see cref="Connection"/>, which throws: a test
/// that reached for the connection would be a test that needs a database, and
/// should say so by carrying the Docker trait instead of silently getting null.
/// </remarks>
internal sealed class NoDatabaseUnitOfWork : IUnitOfWork
{
    public System.Data.Common.DbConnection Connection =>
        throw new NotSupportedException(
            "This host has no database. A test that needs one belongs in the "
            + "Docker-trait suite (see RequiresDocker).");

    public System.Data.Common.DbTransaction? Transaction => null;

    public bool HasActiveTransaction { get; private set; }

    public Task<IUnitOfWorkScope> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        HasActiveTransaction = true;
        return Task.FromResult<IUnitOfWorkScope>(new Frame(this));
    }

    public Task SetTenantContextAsync(
        ITenantContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CommitAsync(CancellationToken cancellationToken = default)
    {
        HasActiveTransaction = false;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        HasActiveTransaction = false;
        return Task.CompletedTask;
    }

    public void MarkRollbackOnly()
    {
        // Nothing to mark: there is no transaction to refuse to commit.
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private sealed class Frame(NoDatabaseUnitOfWork unitOfWork) : IUnitOfWorkScope
    {
        public bool IsOwner => true;

        public Task CompleteAsync(CancellationToken cancellationToken = default) =>
            unitOfWork.CommitAsync(cancellationToken);

        public Task FailAsync(CancellationToken cancellationToken = default) =>
            unitOfWork.RollbackAsync(cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
public sealed class CrossCuttingTestController(IMediator mediator) : ApiControllerBase, ITestOnlyController
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
        throw new ProviderException(
            error: new Error(new LocalizedMessage("lockey_validation_failed")),
            providerName: "test-provider",
            message: "upstream returned 4xx",
            isClientError: true);

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
