using System.Globalization;
using System.Reflection;
using FluentAssertions;
using LearnStack.Infrastructure.DataProtection;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// The storage of ADR-0106 and ADR-0107 repeats <see cref="DataRequestLimits"/> as SQL literals, and classifies the
/// receipt columns: both are read here from the real model and DDL, so a constant changed on one side fails the build
/// instead of drifting.
/// </summary>
public sealed class DataRequestStorageLiteralTests
{
    private const string ModelOnly = "Host=model-only;Database=model-only;Username=model-only";

    [Fact]
    public void Every_bound_the_checks_repeat_is_the_shared_constant()
    {
        using var identity = Identity();
        var model = identity.GetService<IDesignTimeModel>().Model;
        var invariant = CultureInfo.InvariantCulture;

        Check(model, typeof(DataExportArtifact), "ck_data_export_artifacts_size").Should().Be(string.Format(invariant,
            "byte_count BETWEEN 1 AND {0} AND chunk_count BETWEEN 1 AND {1} AND chunk_count = (byte_count + {2}) / {3}",
            DataRequestLimits.MaximumBundleBytes, DataRequestLimits.MaximumChunkCount, DataRequestLimits.ChunkPlaintextBytes - 1,
            DataRequestLimits.ChunkPlaintextBytes));
        Check(model, typeof(DataExportArtifact), "ck_data_export_artifacts_times").Should().Contain(string.Format(invariant,
            "expires_at = created_at + interval '{0} days'", DataRequestLimits.ArtifactLifetime.TotalDays));
        Check(model, typeof(DataExportArtifact), "ck_data_export_artifacts_key_version").Should().Contain(string.Format(invariant,
            "{{1,{0}}}", DataRequestLimits.MaximumKeyVersionLength));
        var truncation = Check(model, typeof(DataExportArtifact), "ck_data_export_artifacts_truncation");
        truncation.Should().Contain(string.Format(invariant, "<= {0}", DataRequestLimits.MaximumTruncatedCollections))
            .And.Contain(DataRequestLimits.MaximumTruncationCount.ToString(invariant))
            .And.Contain(string.Format(invariant, "{{0,{0}}}", DataRequestLimits.MaximumParticipantKeyLength - 1))
            .And.Contain(string.Format(invariant, "{{0,{0}}}", DataRequestLimits.MaximumCollectionNameLength - 1));
        Check(model, typeof(DataExportTicket), "ck_data_export_tickets_times").Should().Contain(string.Format(invariant,
            "expires_at = created_at + interval '{0} minutes'", DataRequestLimits.TicketLifetime.TotalMinutes));
        Check(model, typeof(DataSubjectRequest), "ck_data_subject_requests_window").Should().Contain(string.Format(invariant,
            "execute_at = created_at + interval '{0} hours'", DataRequestLimits.CancellationWindow.TotalHours));
        Check(model, typeof(DataSubjectRequest), "ck_data_subject_requests_snapshot").Should().Contain(string.Format(invariant,
            "BETWEEN 1 AND {0}", DataRequestLimits.MaximumSnapshotMemberships));
        Check(model, typeof(DataSubjectRequest), "ck_data_subject_requests_progress").Should()
            .Contain(string.Format(invariant, "<= {0}", DataRequestLimits.MaximumParticipants))
            .And.Contain(string.Format(invariant, "{{0,{0}}}", DataRequestLimits.MaximumParticipantKeyLength - 1));
        Check(model, typeof(DataSubjectRequest), "ck_data_subject_requests_result").Should()
            .Contain(DataRequestLimits.MaximumTruncationCount.ToString(invariant));

        var chunks = Ddl("ChunkTable");
        chunks.Should().Contain(string.Format(invariant, "chunk_index >= 0 AND chunk_index < {0}", DataRequestLimits.MaximumChunkCount))
            .And.Contain(string.Format(invariant, "octet_length(nonce) = {0}", DataRequestLimits.NonceBytes))
            .And.Contain(string.Format(invariant, "octet_length(tag) = {0}", DataRequestLimits.TagBytes))
            .And.Contain(string.Format(invariant, "octet_length(ciphertext) BETWEEN 1 AND {0}", DataRequestLimits.ChunkPlaintextBytes));
    }

    [Theory]
    [InlineData(nameof(DataSubjectRequest), nameof(DataSubjectRequest.ResultJson))]
    [InlineData(nameof(DataExportArtifact), nameof(DataExportArtifact.ByteCount))]
    [InlineData(nameof(DataExportArtifact), nameof(DataExportArtifact.ChunkCount))]
    [InlineData(nameof(DataExportArtifact), nameof(DataExportArtifact.TruncationJson))]
    public void The_receipts_counts_and_sizes_are_audit_records_of_the_person(string entity, string property)
    {
        using var identity = Identity();
        var type = entity == nameof(DataSubjectRequest) ? typeof(DataSubjectRequest) : typeof(DataExportArtifact);

        identity.Model.FindEntityType(type)!.FindProperty(property)!.FindAnnotation(PersonalDataClassificationExtensions.CategoryAnnotation)!
            .Value.Should().Be(PersonalDataCategory.Audit,
                "how many of a person's records a group holds, and how large their bundle is, describe the person");
    }

    private static string Check(IModel model, Type entity, string name) =>
        model.FindEntityType(entity)!.GetCheckConstraints().SingleOrDefault(check => check.Name == name)?.Sql
        ?? throw new InvalidOperationException("The constraint is not in the model: " + name);

    private static string Ddl(string name) => (string)typeof(IdentityDbContext).Assembly
        .GetType("LearnStack.Modules.Identity.Infrastructure.Persistence.DataRequestStorageDdl", throwOnError: true)!
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;

    private static IdentityDbContext Identity() => new(new DbContextOptionsBuilder<IdentityDbContext>().UseNpgsql(ModelOnly).Options,
        StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);
}
