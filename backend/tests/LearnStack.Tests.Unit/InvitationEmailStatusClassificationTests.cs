using FluentAssertions;
using LearnStack.Infrastructure.Email;
using LearnStack.SharedKernel.Email;
using Xunit;

namespace LearnStack.Tests.Unit;

public sealed class InvitationEmailStatusClassificationTests
{
    [Theory]
    [InlineData("pending", null, false, InvitationEmailDeliveryStatus.Queued)]
    [InlineData("scheduled", null, false, InvitationEmailDeliveryStatus.Queued)]
    [InlineData("executing", null, false, InvitationEmailDeliveryStatus.Unresolved)]
    [InlineData("reconciling", null, false, InvitationEmailDeliveryStatus.Unresolved)]
    [InlineData("terminal_pending", null, false, InvitationEmailDeliveryStatus.Failed)]
    [InlineData("dead_lettered", null, false, InvitationEmailDeliveryStatus.Failed)]
    [InlineData("completed", "succeeded", false, InvitationEmailDeliveryStatus.SubmissionAccepted)]
    [InlineData("completed", "failed", false, InvitationEmailDeliveryStatus.Failed)]
    [InlineData("completed", "anything-else", false, InvitationEmailDeliveryStatus.Failed)]
    // A quarantine outranks every phase and every finalization: the result is not known.
    [InlineData("completed", "succeeded", true, InvitationEmailDeliveryStatus.Unresolved)]
    [InlineData("dead_lettered", "failed", true, InvitationEmailDeliveryStatus.Unresolved)]
    [InlineData("pending", null, true, InvitationEmailDeliveryStatus.Unresolved)]
    public void One_classifier_serves_the_single_and_the_page_read(
        string phase, string? finalization, bool quarantined, InvitationEmailDeliveryStatus expected) =>
        InvitationEmailStatusReader.Classify(phase, finalization, quarantined).Should().Be(expected);

    [Fact]
    public async Task A_page_is_bounded_to_the_invitation_page_limit()
    {
        InvitationEmailStatusReader.MaximumBatch.Should().Be(100);
        var reader = new InvitationEmailStatusReader(null!);
        var tooMany = Enumerable.Range(0, 101).Select(_ => LearnStack.SharedKernel.Jobs.BackgroundJobId.From(Guid.CreateVersion7())).ToArray();
        var act = () => reader.ReadManyAsync(tooMany);
        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }
}
