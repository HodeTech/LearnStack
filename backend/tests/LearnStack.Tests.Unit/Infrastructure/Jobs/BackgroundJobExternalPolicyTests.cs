using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobExternalPolicyTests
{
    [Theory]
    [InlineData(BackgroundJobExternalState.Succeeded, null, 1, BackgroundJobFinalizationStatus.Succeeded, BackgroundJobFinalizationReason.None, null)]
    [InlineData(BackgroundJobExternalState.Succeeded, null, 6, BackgroundJobFinalizationStatus.Succeeded, BackgroundJobFinalizationReason.None, null)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.Permanent, 1, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalFailed, "permanent_failure")]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.Permanent, 6, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalFailed, "permanent_failure")]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, 1, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalFailed, "external_retry_forbidden")]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, 6, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalFailed, "external_retry_forbidden")]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, 1, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalCancelled, "external_retry_forbidden")]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientNotRepeatable, 6, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalCancelled, "external_retry_forbidden")]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientRepeatable, 6, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.NormalAttemptsExhausted, "retry_exhausted")]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientRepeatable, 6, BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.NormalAttemptsExhausted, "retry_exhausted")]
    public void Completed_facts_select_the_explicit_finalization_and_honest_terminal_projection(
        BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy? policy, int starts,
        BackgroundJobFinalizationStatus status, BackgroundJobFinalizationReason reason, string? code)
    {
        BackgroundJobExternalPolicy.Select(state, policy, starts).Should().Be(new BackgroundJobKnownOutcomeDecision(
            BackgroundJobKnownOutcomeDisposition.Finalize, status, reason, code));
    }

    [Theory]
    [InlineData(BackgroundJobExternalState.Failed)]
    [InlineData(BackgroundJobExternalState.Cancelled)]
    public void Only_repeatable_completed_failure_has_the_five_remaining_normal_retry_positions(BackgroundJobExternalState state)
    {
        for (var starts = 1; starts <= 5; starts++)
            BackgroundJobExternalPolicy.Select(state, BackgroundJobExternalFailurePolicy.TransientRepeatable, starts)
                .Should().Be(new BackgroundJobKnownOutcomeDecision(BackgroundJobKnownOutcomeDisposition.Retry, null, null, null));
    }

    [Theory]
    [InlineData(BackgroundJobExternalState.Running, null, 1)]
    [InlineData(BackgroundJobExternalState.Unknown, null, 1)]
    [InlineData(BackgroundJobExternalState.NeverStarted, null, 1)]
    [InlineData(BackgroundJobExternalState.Failed, null, 1)]
    [InlineData(BackgroundJobExternalState.Cancelled, null, 1)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.Permanent, 1)]
    [InlineData(BackgroundJobExternalState.Succeeded, BackgroundJobExternalFailurePolicy.TransientRepeatable, 1)]
    [InlineData(BackgroundJobExternalState.Failed, (BackgroundJobExternalFailurePolicy)0, 1)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientRepeatable, 0)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientRepeatable, 7)]
    public void Missing_invalid_unresolved_or_uncounted_facts_cannot_default_into_a_disposition(
        BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy? policy, int starts)
    {
        var select = () => BackgroundJobExternalPolicy.Select(state, policy, starts);
        select.Should().Throw<ArgumentException>();
    }
}
