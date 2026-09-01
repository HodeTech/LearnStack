using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.ErrorTracking;
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
/// The same defect <c>UnassignedActorIdTests</c> covers for the actor, for the
/// two ids that became value objects in Packet 7 step 2: an
/// <c>ITenantContext</c> that reports <c>IsResolved</c> while carrying a
/// <c>TenantId</c> or <c>OrganizationId</c> nothing ever assigned.
/// </summary>
/// <remarks>
/// <para>
/// These exist because the gates they cover were <b>deleted by mutation and the
/// whole suite stayed green</b>. Stripping the <c>IsInitialized()</c> check from
/// the span processor, the Serilog enricher and <c>LoggingBehavior</c> at once —
/// reverting all three to the shape they shipped in before the review round that
/// added them — changed nothing any test could see. A guard nothing kills is a
/// comment.
/// </para>
/// <para>
/// The state is reachable by omission rather than by a literal: Vogen's VOG009
/// rejects <c>default(TenantId)</c>, so it arrives through an array element, a
/// <c>default(T)</c> in a generic, or a member a deserializer skipped.
/// <c>ITenantContext</c> documents <c>IsResolved</c> as implying an initialized
/// <c>TenantId</c>; these cases are what stops that promise from being the only
/// thing standing between a bad context and four paths that must not throw.
/// Packet 7's <c>TenantResolverMiddleware</c> is the first component that builds
/// a resolved context rather than stubbing one, which is why they land now.
/// </para>
/// </remarks>
public sealed class UnassignedTenantIdTests
{
    private static readonly TestTenantContext ResolvedWithUnassignedIds = new()
    {
        IsResolved = true,
        TenantId = Zeroed<TenantId>(),
        OrganizationId = Zeroed<OrganizationId>(),
        UserId = UserId.From(Guid.Parse("018f4d40-1234-7000-8000-000000000003")),
        CorrelationId = "00-aabbccdd-eeff0011-01",
        ModuleName = "education",
    };

    [Fact]
    public void The_Fixture_Really_Is_Present_But_Unassigned()
    {
        // Guards the guards. If a Vogen upgrade ever made an array element's
        // default value initialized, every case below would keep passing while
        // exercising nothing — the one failure a test cannot report itself.
        ResolvedWithUnassignedIds.IsResolved.Should().BeTrue();
        ResolvedWithUnassignedIds.TenantId.IsInitialized().Should().BeFalse();
        ResolvedWithUnassignedIds.OrganizationId.Should().NotBeNull();
        ResolvedWithUnassignedIds.OrganizationId!.Value.IsInitialized().Should().BeFalse();
    }

    [Fact]
    public void SpanProcessor_DoesNotThrow_And_Omits_Both_Tags()
    {
        var processor = new TenantContextSpanProcessor(
            new TestAccessor(ResolvedWithUnassignedIds));

        using var activity = new Activity("unassigned-tenant");
        activity.Start();

        var act = () => processor.OnStart(activity);

        act.Should().NotThrow();
        activity.GetTagItem("tenant.id").Should().BeNull();
        activity.GetTagItem("organization.id").Should().BeNull();
        activity.GetTagItem("user.id").Should().NotBeNull("the rest still enriches");
    }

    [Fact]
    public void SerilogEnricher_DoesNotThrow_And_Omits_Both_Properties()
    {
        var enricher = new CorrelationContextEnricher(
            new TestAccessor(ResolvedWithUnassignedIds));

        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplate("test", []),
            []);

        var act = () => enricher.Enrich(logEvent, new SimplePropertyFactory());

        act.Should().NotThrow();
        logEvent.Properties.ContainsKey("tenant.id").Should().BeFalse();
        logEvent.Properties.ContainsKey("organization.id").Should().BeFalse();
        logEvent.Properties.ContainsKey("user.id").Should().BeTrue("the rest still enriches");
    }

    [Fact]
    public async Task LoggingBehavior_DoesNotThrow_Building_Its_Scope()
    {
        // BuildScope runs at pipeline step 2, outside any try of its own, so a
        // throw here fails the request it was only meant to describe.
        var behavior = new LoggingBehavior<DummyCommand, Result<string>>(
            NullLogger<LoggingBehavior<DummyCommand, Result<string>>>.Instance,
            new TestAccessor(ResolvedWithUnassignedIds));

        RequestHandlerDelegate<Result<string>> next = () => Task.FromResult(Result.Ok("ok"));

        var act = async () => await behavior.Handle(new DummyCommand(), next, default);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ExceptionHandler_Reports_Absent_Ids_Rather_Than_Zero_Ones()
    {
        var tracker = new RecordingErrorTracker();
        var handler = new LearnStackExceptionHandler(
            tracker,
            new TestAccessor(ResolvedWithUnassignedIds),
            NullLogger<LearnStackExceptionHandler>.Instance);

        var httpContext = new DefaultHttpContext();
        httpContext.Response.Body = new MemoryStream();

        var act = async () => await handler.TryHandleAsync(
            httpContext, new InvalidOperationException("boom"), default);

        await act.Should().NotThrowAsync();
        tracker.LastContext.Should().NotBeNull();
        tracker.LastContext!.OrganizationId.Should().BeNull(
            "an unassigned organization is absent, not a zero Guid");
    }

    [Fact]
    public async Task LocalFileErrorTracker_Writes_The_Envelope_Instead_Of_Dropping_It()
    {
        // The consequence this one guards is the quietest: Vogen's JSON converter
        // reads Value, so an unassigned id makes Serialize throw — and this
        // tracker's catch swallows it, dropping the whole envelope to a Warning
        // while the caller logs the capture as a success. On an air-gapped
        // deployment that file is the only record the error ever had.
        var directory = Path.Combine(Path.GetTempPath(), $"ls-unassigned-{Guid.NewGuid():N}");

        try
        {
            var sut = new LocalFileErrorTracker(
                directory, NullLogger<LocalFileErrorTracker>.Instance);

            await sut.CaptureAsync(
                new InvalidOperationException("boom"),
                new CapturedContext(
                    CorrelationId: "00-aabb-ccdd-01",
                    RequestPath: "/api/v1/probe",
                    RequestMethod: "GET",
                    TenantId: Zeroed<TenantId>(),
                    OrganizationId: Zeroed<OrganizationId>(),
                    UserId: null,
                    ModuleName: "education"));

            var files = Directory.GetFiles(directory);
            files.Should().HaveCount(1, "the envelope must be written, not swallowed");

            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(files[0]));
            doc.RootElement.GetProperty("TenantId").ValueKind.Should().Be(JsonValueKind.Null);
            doc.RootElement.GetProperty("OrganizationId").ValueKind.Should().Be(JsonValueKind.Null);
            doc.RootElement.GetProperty("exception").GetProperty("message").GetString()
                .Should().Be("boom", "the part that matters still reaches the file");
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    /// <summary>
    /// An id in the state nothing can write as a literal — VOG009 rejects
    /// <c>default(TId)</c> and VOG010 rejects <c>new TId()</c>. An array's
    /// elements are zeroed by the runtime, which neither analyzer inspects.
    /// </summary>
    private static TId Zeroed<TId>()
        where TId : struct
    {
        var slot = new TId[1];
        return slot[0];
    }

    public sealed record DummyCommand : IRequest<Result<string>>;

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
        public LogEventProperty CreateProperty(
            string name, object? value, bool destructureObjects = false) =>
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
