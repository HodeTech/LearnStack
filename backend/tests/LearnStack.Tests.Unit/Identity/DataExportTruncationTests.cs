using FluentAssertions;
using LearnStack.Modules.Identity.Application.DataRequests;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using LearnStack.SharedKernel.DataProtection;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>ADR-0107 § 3: the receipt's closed truncation statement; ADR-0106 § 8: a dead job reads as failed.</summary>
public sealed class DataExportTruncationTests
{
    [Fact]
    public void A_truncation_statement_round_trips_in_one_canonical_form()
    {
        var statement = new[] { new DataExportTruncation("tenancy.field_consents", 1, 4), new DataExportTruncation("audit.entries", 10, 25) };

        var json = DataRequestDocuments.WriteTruncation(statement);

        json.Should().Be("""{"audit.entries":{"included":10,"total":25},"tenancy.field_consents":{"included":1,"total":4}}""");
        DataRequestDocuments.ReadTruncation(json!).Should().Equal(statement.OrderBy(item => item.Collection, StringComparer.Ordinal));
        DataRequestDocuments.WriteTruncation([]).Should().Be("{}");
        DataRequestDocuments.ReadTruncation("{}").Should().BeEmpty();
    }

    [Theory]
    [InlineData("audit", 0, 1)]
    [InlineData("Audit.entries", 0, 1)]
    [InlineData("audit.entries.more", 0, 1)]
    [InlineData(".entries", 0, 1)]
    [InlineData("audit.", 0, 1)]
    [InlineData("audit.entries", 2, 1)]
    [InlineData("audit.entries", -1, 1)]
    public void A_truncation_names_one_section_collection_with_included_at_most_total(string collection, long included, long total) =>
        FluentActions.Invoking(() => new DataExportTruncation(collection, included, total)).Should().Throw<ArgumentException>();

    [Fact]
    public void A_statement_is_bounded_and_names_each_collection_once()
    {
        var repeated = new[] { new DataExportTruncation("audit.entries", 1, 2), new DataExportTruncation("audit.entries", 1, 3) };
        var oversized = Enumerable.Range(0, DataRequestLimits.MaximumTruncatedCollections + 1)
            .Select(index => new DataExportTruncation("audit.c" + index, 0, 1)).ToArray();

        DataRequestDocuments.WriteTruncation(repeated).Should().BeNull();
        DataRequestDocuments.WriteTruncation(oversized).Should().BeNull();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"audit.entries\":1}")]
    [InlineData("{\"audit.entries\":{\"included\":1}}")]
    [InlineData("{\"audit.entries\":{\"included\":1,\"total\":2,\"extra\":3}}")]
    [InlineData("{\"audit.entries\":{\"included\":3,\"total\":2}}")]
    [InlineData("{\"bad\":{\"included\":1,\"total\":2}}")]
    public void A_stored_statement_outside_the_closed_shape_is_corrupt_storage(string json) =>
        FluentActions.Invoking(() => DataRequestDocuments.ReadTruncation(json)).Should().Throw<InvalidOperationException>();

    [Theory]
    [InlineData("pending", false, "Pending")]
    [InlineData("executing", false, "Pending")]
    [InlineData("reconciling", false, "Pending")]
    [InlineData("completed", false, "Completed")]
    [InlineData("completed", true, "Dead")]
    [InlineData("terminal_pending", false, "Dead")]
    [InlineData("quarantined", false, "Dead")]
    [InlineData("executing", true, "Dead")]
    public void A_quarantined_dead_lettered_or_terminally_failed_job_is_dead(string phase, bool failed, string expected) =>
        DataRequestJobPhaseReader.Classify(phase, failed).Should().Be(Enum.Parse<DataRequestJobPhase>(expected));
}
