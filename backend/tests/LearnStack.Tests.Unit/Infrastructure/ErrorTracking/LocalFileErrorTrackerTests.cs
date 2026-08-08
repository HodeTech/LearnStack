using System.Text.Json;
using FluentAssertions;
using LearnStack.Infrastructure.ErrorTracking;
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
            TenantId: Guid.NewGuid(),
            OrganizationId: null,
            UserId: null,
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
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
