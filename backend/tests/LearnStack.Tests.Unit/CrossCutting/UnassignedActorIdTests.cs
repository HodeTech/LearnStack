using System.Diagnostics;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Observability;
using LearnStack.Infrastructure.Observability.Serilog;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Observability;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace LearnStack.Tests.Unit.CrossCutting;

/// <summary>
/// One defect, four consumers: <c>ITenantContext.UserId</c> is a
/// <c>UserId?</c>, so <c>HasValue == true</c> says a struct is present, not
/// that <c>From(...)</c> ever ran on it. Reading <c>.Value</c> on the
/// unassigned struct throws, and every cross-cutting reader of the actor id
/// sits on a path that must not throw — a span processor inside
/// <c>Activity.Start()</c>, a Serilog enricher inside the log line it
/// enriches, pipeline step 2 before the handler, and the L1 exception
/// handler, which is already handling something else.
/// </summary>
/// <remarks>
/// The state is reachable by omission, not by a literal a grep can find: any
/// context built from a record or DTO whose <c>UserId</c> property nothing
/// assigns is in it. Packet 7's resolver is the first producer, so these
/// four sites are unreachable only for as long as
/// <see cref="UnresolvedTenantContext"/> hard-codes <c>null</c>. The same
/// shape is asserted for aggregates by
/// <c>AuditableEntityTests.CommandWithUnassignedActor</c>.
/// </remarks>
public sealed class UnassignedActorIdTests
{
    // The unassigned state has no literal that can express it: Vogen's VOG009
    // rejects `default(UserId)` and VOG010 rejects `new UserId()`. It is reached
    // only the way production reaches it - a non-nullable auto-property nothing
    // assigns, widened to UserId? on the way in. That is also why the call sites
    // could not be found by grepping for a token.
    private static readonly UnassignedActorCommand Command = new("any");

    private static readonly TestTenantContext ContextWithUnassignedActor = new()
    {
        UserId = Command.ActorId,
        IsResolved = true,
        TenantId = TenantId.From(Guid.Parse("018f4d40-1234-7000-8000-000000000001")),
        OrganizationId = null,
        CorrelationId = "00-aabbccdd-eeff0011-01",
        ModuleName = "education",
    };

    [Fact]
    public void The_Fixture_Really_Is_Present_But_Unassigned()
    {
        // Guards the guards: if Vogen ever made default(UserId) initialized,
        // every assertion below would pass without exercising anything.
        ContextWithUnassignedActor.UserId.HasValue.Should().BeTrue();
        ContextWithUnassignedActor.UserId!.Value.IsInitialized().Should().BeFalse();
    }

    [Fact]
    public void SpanProcessor_DoesNotThrow_And_Omits_The_Tag()
    {
        var processor = new TenantContextSpanProcessor(
            new TestAccessor(ContextWithUnassignedActor));

        using var activity = new Activity("unassigned-actor");
        activity.Start();

        var act = () => processor.OnStart(activity);

        act.Should().NotThrow();
        activity.GetTagItem("user.id").Should().BeNull();
        activity.GetTagItem("tenant.id").Should().NotBeNull("the rest still enriches");
    }

    [Fact]
    public void SerilogEnricher_DoesNotThrow_And_Omits_The_Property()
    {
        var enricher = new CorrelationContextEnricher(
            new TestAccessor(ContextWithUnassignedActor));

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplate("test", []),
            []);

        var act = () => enricher.Enrich(logEvent, new SimplePropertyFactory());

        act.Should().NotThrow();
        logEvent.Properties.ContainsKey("user.id").Should().BeFalse();
        logEvent.Properties.ContainsKey("tenant.id").Should().BeTrue("the rest still enriches");
    }

    [Fact]
    public async Task LoggingBehavior_DoesNotThrow_Building_Its_Scope()
    {
        var behavior = new LoggingBehavior<DummyCommand, Result<string>>(
            NullLogger<LoggingBehavior<DummyCommand, Result<string>>>.Instance,
            new TestAccessor(ContextWithUnassignedActor));

        RequestHandlerDelegate<Result<string>> next = () => Task.FromResult(Result.Ok("ok"));

        var act = async () => await behavior.Handle(new DummyCommand(), next, default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExceptionHandler_DoesNotThrow_And_Reports_A_Null_Actor()
    {
        var tracker = new RecordingErrorTracker();
        var handler = new LearnStackExceptionHandler(
            tracker,
            new TestAccessor(ContextWithUnassignedActor),
            NullLogger<LearnStackExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var act = async () => await handler.TryHandleAsync(
            httpContext, new InvalidOperationException("boom"), default);

        await act.Should().NotThrowAsync();
        tracker.LastContext.Should().NotBeNull();
        tracker.LastContext!.UserId.Should().BeNull(
            "an unassigned actor is absent, not a zero Guid");
    }

    public sealed record DummyCommand : IRequest<Result<string>>;

    private sealed record UnassignedActorCommand(string Title)
    {
        public UserId ActorId { get; init; }
    }

    private sealed class TestAccessor(ITenantContext? current) : ITenantContextAccessor
    {
        public ITenantContext? Current { get; set; } = current;
    }

    private sealed record TestTenantContext : ITenantContext
    {
        public bool IsResolved { get; init; }

        public TenantId TenantId { get; init; }

        public OrganizationId? OrganizationId { get; init; }

        public UserId? UserId { get; init; }

        public string? CorrelationId { get; init; }

        public string? ModuleName { get; init; }
    }

    private sealed class SimplePropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, value as LogEventPropertyValue ?? new ScalarValue(value));
    }

    private sealed class RecordingErrorTracker : IErrorTrackingProvider
    {
        public CapturedContext? LastContext { get; private set; }

        public ValueTask CaptureAsync(
            Exception exception,
            CapturedContext context,
            CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return ValueTask.CompletedTask;
        }
    }
}
