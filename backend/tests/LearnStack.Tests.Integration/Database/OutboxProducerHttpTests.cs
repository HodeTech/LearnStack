using System.Diagnostics;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Persistence;
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

namespace LearnStack.Tests.Integration.Database;

public sealed partial class OutboxProducerTests
{
    /// <summary>
    /// <see href="../../../../docs/decisions/0057-transactional-event-production-and-completion.md">ADR-0057</see>
    /// preserves the HTTP producer's trace and successful transaction announcement in
    /// the committed outbox. The test-only transport makes no authorization or
    /// production-route admission claim.
    /// </summary>
    [Fact]
    [Trait(RequiresDocker.Key, RequiresDocker.Value)]
    public async Task An_HTTP_request_commits_the_enqueuing_trace_announced_scope_and_exact_partition()
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        var observation = new HttpProducerObservation();
        await using var host = new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.AddControllers(options => options.Conventions.Insert(0,
                        new TestControllerFilter(typeof(OutboxHttpProbeController))))
                    .AddApplicationPart(typeof(OutboxHttpProbeController).Assembly);
                services.AddSingleton(observation);
                services.RemoveAll<IOutbox>();
                services.AddScoped<PostgresOutbox>();
                services.AddScoped<IOutbox, HttpObservedOutbox>();
            });
        });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://localhost"),
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/outbox-http-probe");
        request.Headers.Add("traceparent", AmbientCorrelation);
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        observation.Calls.Should().Be(1, "actual provisioning translates exactly one saved fact");
        observation.UnresolvedAtEntry.Should().BeTrue(
            "the test-only caller uses existing unresolved provisioning and tracing supplies no authority");
        observation.Announcement.Should().NotBeNull();
        var announcement = observation.Announcement!;
        announcement.TenantId.Value.Should().Be(SchemaFixture.TenantA);
        announcement.OrganizationId.Should().BeNull("provisioning deliberately announces a tenant-wide transaction");
        var row = (await ReadRowAsync(database.AppConnectionString, observation.EventId))!;
        row.Should().NotBeNull("a separate application connection must observe the committed row");
        row.TenantId.Should().Be(announcement.TenantId.Value);
        row.OrganizationId.Should().Be(announcement.OrganizationId?.Value);
        row.PartitionKey.Should().NotBeNullOrEmpty().And.Be(observation.PartitionKey);
        row.CorrelationId.Should().Be(observation.EnqueueActivity);
        ActivityContext.TryParse(row.CorrelationId, null, out var persisted).Should().BeTrue();
        ActivityContext.TryParse(observation.EnqueueActivity, null, out var enqueuing).Should().BeTrue();
        ActivityContext.TryParse(observation.HttpActivity, null, out var http).Should().BeTrue();
        ActivityContext.TryParse(AmbientCorrelation, null, out var incoming).Should().BeTrue();
        persisted.TraceId.Should().Be(enqueuing.TraceId).And.Be(http.TraceId).And.Be(incoming.TraceId);
    }

    private sealed class HttpObservedOutbox(
        PostgresOutbox actual, IUnitOfWork unit, HttpProducerObservation observation) : IOutbox
    {
        public async Task EnqueueAsync(IIntegrationEvent integrationEvent, CancellationToken cancellationToken = default)
        {
            observation.Calls++;
            observation.EventId = integrationEvent.EventId;
            observation.PartitionKey = integrationEvent.PartitionKey;
            observation.EnqueueActivity = Activity.Current?.Id;
            observation.Announcement = unit.GetTenantContextAnnouncement(unit.Transaction);
            (await ScalarInUnitAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
            (await ScalarInUnitAsync(unit,
                "SELECT (rolbypassrls OR rolsuper)::text FROM pg_roles WHERE rolname = current_user"))
                .Should().Be("false");
            await actual.EnqueueAsync(integrationEvent, cancellationToken);
        }
    }
}

/// <summary>Observation only; no request metadata supplies tenancy or transaction authority.</summary>
internal sealed class HttpProducerObservation
{
    public int Calls { get; set; }
    public Guid EventId { get; set; }
    public string? PartitionKey { get; set; }
    public string? HttpActivity { get; set; }
    public string? EnqueueActivity { get; set; }
    public TenantContextAnnouncement? Announcement { get; set; }
    public bool UnresolvedAtEntry { get; set; }
}

/// <summary>
/// Test-only HTTP transport for the existing tests-only provisioning caller. It does
/// not introduce a production route or exercise authorization/route admission policy.
/// </summary>
[Route("outbox-http-probe")]
public sealed class OutboxHttpProbeController(ISender sender, IServiceProvider services)
    : ApiControllerBase, ITestOnlyController
{
    [HttpPost]
    public async Task<IActionResult> Produce(CancellationToken cancellationToken)
    {
        var observation = services.GetRequiredService<HttpProducerObservation>();
        observation.HttpActivity = Activity.Current?.Id;
        observation.UnresolvedAtEntry = !services.GetRequiredService<ITenantContext>().IsResolved;
        var result = await sender.Send(new ProvisionTenantCommand(
            TenantId.From(SchemaFixture.TenantA), "http-producer-proof", "HTTP producer proof",
            OrganizationId.From(Guid.CreateVersion7()), "hq", "HQ"), cancellationToken);
        return result.ToActionResult();
    }
}
