using FluentAssertions;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>ADR-0107 § 4's artifact lifecycle and session-bound download tickets.</summary>
public sealed class DataExportArtifactDomainTests
{
    private static readonly DateTimeOffset At = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly UserId Subject = UserId.From(Guid.CreateVersion7());
    private static readonly UserId Other = UserId.From(Guid.CreateVersion7());
    private static readonly string TicketDigest = new('a', 64);
    private static readonly string SessionDigest = new('b', 64);

    [Theory]
    [InlineData(1L, 1)]
    [InlineData(131072L, 1)]
    [InlineData(131073L, 2)]
    [InlineData(16L * 1024 * 1024, 128)]
    [InlineData(64L * 1024 * 1024, 512)]
    public void An_artifact_of_a_fulfilling_export_mirrors_its_request_and_counts_its_chunks(long bytes, int chunks)
    {
        var request = FulfillingExport(out var at);
        var artifact = DataExportArtifact.Create(NewArtifactId(), request, bytes, "v1", at, UserId.SystemActor).Value!;

        artifact.Status.Should().Be(DataExportArtifactStatus.Available);
        artifact.ChunkCount.Should().Be(chunks);
        artifact.TenantId.Should().Be(request.TenantId);
        artifact.OrganizationId.Should().Be(request.OrganizationId);
        artifact.SubjectUserId.Should().Be(Subject);
        artifact.RequestId.Should().Be(request.Id);
        artifact.ExpiresAt.Should().Be(at + TimeSpan.FromDays(7), "ADR-0107 § 4: readable for seven days");
        artifact.IsReadable(at).Should().BeTrue();
        artifact.IsReadable(artifact.ExpiresAt).Should().BeFalse("expiry is a time check, whether or not the purge ran");
    }

    [Fact]
    public void An_artifact_belongs_only_to_a_fulfilling_export_and_a_bounded_bundle()
    {
        var requested = DataSubjectRequest.FileForSelf(NewRequestId(), DataRequestKind.Export, Subject, Group(), [NewMembership()], At).Value!;
        DataExportArtifact.Create(NewArtifactId(), requested, 10, "v1", At.AddMinutes(1), UserId.SystemActor).IsFailure.Should().BeTrue();
        var erasure = DataSubjectRequest.FileForSelf(NewRequestId(), DataRequestKind.Erasure, Subject, Group(), [NewMembership()], At).Value!;
        erasure.StartFulfilling(erasure.ExecuteAt!.Value, UserId.SystemActor).IsSuccess.Should().BeTrue();
        DataExportArtifact.Create(NewArtifactId(), erasure, 10, "v1", erasure.ExecuteAt!.Value, UserId.SystemActor).IsFailure.Should().BeTrue();

        var export = FulfillingExport(out var at);
        DataExportArtifact.Create(NewArtifactId(), export, 0, "v1", at, UserId.SystemActor).IsFailure.Should().BeTrue();
        DataExportArtifact.Create(NewArtifactId(), export, DataRequestLimits.MaximumBundleBytes + 1, "v1", at, UserId.SystemActor)
            .IsFailure.Should().BeTrue("ADR-0107 § 4: 64 MiB at most");
        DataExportArtifact.Create(NewArtifactId(), export, 10, "key/1", at, UserId.SystemActor).IsFailure.Should().BeTrue();
        DataExportArtifact.Create(NewArtifactId(), export, 10, new string('k', 33), at, UserId.SystemActor).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Revocation_purges_an_unexpired_artifact_at_once_and_is_final()
    {
        var artifact = Artifact(out var at);
        artifact.Revoke(at.AddHours(1), Subject).IsSuccess.Should().BeTrue();
        artifact.Status.Should().Be(DataExportArtifactStatus.Revoked);
        artifact.PurgedAt.Should().Be(at.AddHours(1));
        artifact.Version.Should().Be(1);
        artifact.Revoke(at.AddHours(2), Subject).IsFailure.Should().BeTrue();
        artifact.Purge(artifact.ExpiresAt, UserId.SystemActor).IsFailure.Should().BeTrue("a revoked artifact holds nothing to purge");

        var expired = Artifact(out at);
        expired.Revoke(expired.ExpiresAt, Subject).IsFailure.Should().BeTrue("only an unexpired artifact is revoked");
    }

    [Fact]
    public void Expiry_and_purge_follow_the_expiry_instant()
    {
        var artifact = Artifact(out var at);
        artifact.Expire(artifact.ExpiresAt.AddTicks(-1), UserId.SystemActor).IsFailure.Should().BeTrue();
        artifact.Purge(artifact.ExpiresAt.AddTicks(-1), UserId.SystemActor).IsFailure.Should().BeTrue();
        artifact.Expire(artifact.ExpiresAt, UserId.SystemActor).IsSuccess.Should().BeTrue();
        artifact.PurgedAt.Should().BeNull("an expired artifact may still hold chunks until the purge");
        artifact.Purge(artifact.ExpiresAt.AddMinutes(5), UserId.SystemActor).IsSuccess.Should().BeTrue();
        artifact.Status.Should().Be(DataExportArtifactStatus.Purged);
        artifact.PurgedAt.Should().Be(artifact.ExpiresAt.AddMinutes(5));
        artifact.Expire(artifact.ExpiresAt.AddDays(1), UserId.SystemActor).IsFailure.Should().BeTrue();

        var direct = Artifact(out at);
        direct.Purge(direct.ExpiresAt, UserId.SystemActor).IsSuccess.Should().BeTrue("the purge job may find it still available");
    }

    [Fact]
    public void Only_the_subject_mints_a_ticket_for_a_readable_artifact_and_it_lasts_fifteen_minutes()
    {
        var artifact = Artifact(out var at);
        DataExportTicket.Mint(NewTicketId(), artifact, TicketDigest, SessionDigest, at, Other).IsFailure.Should().BeTrue(
            "ADR-0106 § 6: the recipient of an export is always the subject");
        DataExportTicket.Mint(NewTicketId(), artifact, TicketDigest, SessionDigest, at, UserId.SystemActor).IsFailure.Should().BeTrue();
        DataExportTicket.Mint(NewTicketId(), artifact, "A".PadLeft(64, 'A'), SessionDigest, at, Subject).IsFailure.Should().BeTrue();
        DataExportTicket.Mint(NewTicketId(), artifact, TicketDigest, "short", at, Subject).IsFailure.Should().BeTrue();

        var ticket = DataExportTicket.Mint(NewTicketId(), artifact, TicketDigest, SessionDigest, at, Subject).Value!;
        ticket.ExpiresAt.Should().Be(at + TimeSpan.FromMinutes(15));
        ticket.CreatedBy.Should().Be(Subject);
        ticket.ArtifactId.Should().Be(artifact.Id);
        ticket.OrganizationId.Should().Be(artifact.OrganizationId);
        ticket.Matches(TicketDigest, SessionDigest).Should().BeTrue();
        ticket.Matches(TicketDigest, TicketDigest).Should().BeFalse("the ticket is bound to the session that minted it");
        ticket.Matches(SessionDigest, SessionDigest).Should().BeFalse();

        artifact.Revoke(at.AddMinutes(1), Subject).IsSuccess.Should().BeTrue();
        DataExportTicket.Mint(NewTicketId(), artifact, TicketDigest, SessionDigest, at.AddMinutes(2), Subject).IsFailure.Should().BeTrue(
            "a revoked artifact is not readable");
    }

    [Fact]
    public void A_ticket_is_consumed_once_inside_its_lifetime()
    {
        var artifact = Artifact(out var at);
        var ticket = DataExportTicket.Mint(NewTicketId(), artifact, TicketDigest, SessionDigest, at, Subject).Value!;
        ticket.Consume(ticket.ExpiresAt).IsFailure.Should().BeTrue();
        ticket.Consume(at.AddMinutes(14)).IsSuccess.Should().BeTrue();
        ticket.ConsumedAt.Should().Be(at.AddMinutes(14));
        ticket.IsUsable(at.AddMinutes(14)).Should().BeFalse("a consumed ticket authorizes no further read");
        ticket.Consume(at.AddMinutes(14)).IsFailure.Should().BeTrue("a resumed download mints a new ticket");
    }

    private static DataSubjectRequest FulfillingExport(out DateTimeOffset at)
    {
        var request = DataSubjectRequest.FileForSelf(NewRequestId(), DataRequestKind.Export, Subject, Group(), [NewMembership()], At).Value!;
        at = At.AddMinutes(1);
        request.StartFulfilling(at, UserId.SystemActor).IsSuccess.Should().BeTrue();
        at = at.AddMinutes(1);
        return request;
    }

    private static DataExportArtifact Artifact(out DateTimeOffset at) =>
        DataExportArtifact.Create(NewArtifactId(), FulfillingExport(out at), 4096, "v1", at, UserId.SystemActor).Value!;

    private static DataRequestScopeGroup Group() => new(Tenant, null);
    private static MembershipId NewMembership() => MembershipId.From(Guid.CreateVersion7());
    private static DataSubjectRequestId NewRequestId() => DataSubjectRequestId.From(Guid.CreateVersion7());
    private static DataExportArtifactId NewArtifactId() => DataExportArtifactId.From(Guid.CreateVersion7());
    private static DataExportTicketId NewTicketId() => DataExportTicketId.From(Guid.CreateVersion7());
}
