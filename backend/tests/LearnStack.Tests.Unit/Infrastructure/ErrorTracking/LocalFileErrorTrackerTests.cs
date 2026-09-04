using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.ErrorTracking;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Observability;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.ErrorTracking;

/// <summary>
/// LocalFileErrorTracker is the air-gapped capture path per ADR-0032 §
/// Sub-decision 9. The capture must be best-effort: write a JSON envelope
/// if possible, swallow filesystem failures so the L1 handler still
/// returns the Problem Details body to the client.
/// </summary>
public sealed class LocalFileErrorTrackerTests : IDisposable
{
    private readonly string _directory;

    public LocalFileErrorTrackerTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(),
            "learnstack-errortracker-tests",
            Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public async Task CaptureAsync_Writes_JsonEnvelope_To_ConfiguredDirectory()
    {
        var sut = new LocalFileErrorTracker(_directory, NullLogger<LocalFileErrorTracker>.Instance);
        var context = new CapturedContext(
            CorrelationId: "00-aabb-ccdd-01",
            RequestPath: "/v1/courses",
            RequestMethod: "POST",
            TenantId: TenantId.From(TenantGuid),
            OrganizationId: null,
            UserId: UserId.From(UserGuid),
            ModuleName: "education");

        await sut.CaptureAsync(new InvalidOperationException("boom"), context);

        var files = Directory.GetFiles(_directory);
        files.Should().HaveCount(1);

        var raw = await File.ReadAllTextAsync(files[0]);
        var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("CorrelationId").GetString().Should().Be("00-aabb-ccdd-01");
        doc.RootElement.GetProperty("RequestPath").GetString().Should().Be("/v1/courses");
        doc.RootElement.GetProperty("exception").GetProperty("type").GetString()
            .Should().Be(typeof(InvalidOperationException).FullName);

        // The envelope is a wire format an operator greps, and the ids on it
        // became value objects rather than raw Guids. Vogen's System.Text.Json
        // converter unwraps them to the bare value, so this file is byte-identical
        // to what it held before — asserted rather than assumed, because a
        // converter that ever emitted {"Value":"..."} would break every existing
        // query against these files and no other test would notice.
        doc.RootElement.GetProperty("TenantId").GetString().Should().Be(TenantGuid.ToString());
        doc.RootElement.GetProperty("UserId").GetString().Should().Be(UserGuid.ToString());
        doc.RootElement.GetProperty("OrganizationId").ValueKind
            .Should().Be(JsonValueKind.Null, "a tenant-wide capture carries no organization");
    }

    private static readonly Guid TenantGuid =
        Guid.Parse("018f4d40-1234-7000-8000-0000000000a1");

    private static readonly Guid UserGuid =
        Guid.Parse("018f4d40-1234-7000-8000-0000000000a2");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
