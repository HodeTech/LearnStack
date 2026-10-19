using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobRetainedExternalObservationTests
{
    private const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid Artifact = Guid.Parse("01234567-89ab-7cde-8f01-23456789abcd");

    [Theory]
    [InlineData(BackgroundJobExternalState.Succeeded, null)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.Permanent)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientRepeatable)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientNotRepeatable)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientRepeatable)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientNotRepeatable)]
    public void Completed_observation_freezes_identifier_bytes_and_policy_before_provider_owned_objects_can_change(
        BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy? policy)
    {
        var outcome = new Outcome(Artifact);
        var evidence = new BackgroundJobQuiescenceEvidence(Guid.NewGuid(), BackgroundJobQuiescenceKind.ProviderFenced);
        var report = state switch
        {
            BackgroundJobExternalState.Succeeded => BackgroundJobExternalResult.Succeeded(outcome, evidence),
            BackgroundJobExternalState.Failed => BackgroundJobExternalResult.Failed(outcome, evidence, policy!.Value),
            BackgroundJobExternalState.Cancelled => BackgroundJobExternalResult.Cancelled(outcome, evidence, policy!.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        var context = Context();
        using var boundary = BackgroundJobExecutionAdmission.EnterExternal();
        var retained = BackgroundJobRetainedExternalObservation.Capture(context, Fingerprint, report);
        retained.JobId.Should().Be(context.JobId);
        retained.AttemptId.Should().Be(context.AttemptId);
        retained.OperationId.Should().Be(context.OperationId);
        retained.PlanFingerprint.Should().Be(Fingerprint);
        retained.State.Should().Be(state);
        retained.FailurePolicy.Should().Be(policy);
        retained.QuiescenceId.Should().Be(evidence.EvidenceId);
        retained.QuiescenceKind.Should().Be(evidence.Kind);
        retained.IsCompleted.Should().BeTrue();
        var expected = "{\"ArtifactId\":\"" + Artifact.ToString("D") + "\"}";
        retained.OutcomeJson.Should().Be(expected);
        // Adversarial fixture only: even an adapter bypassing init-only with
        // reflection after return cannot mutate the already captured bytes.
        var changed = Guid.NewGuid();
        typeof(Outcome).GetProperty(nameof(Outcome.ArtifactId))!.SetValue(outcome, changed);
        report.Outcome!.ArtifactId.Should().Be(changed);
        retained.OutcomeJson.Should().Be(expected);
    }

    [Theory]
    [InlineData(BackgroundJobExternalState.Running)]
    [InlineData(BackgroundJobExternalState.Unknown)]
    [InlineData(BackgroundJobExternalState.NeverStarted)]
    public void Unresolved_reports_remain_memory_observations_without_outcome_or_release_facts(BackgroundJobExternalState state)
    {
        var report = state switch
        {
            BackgroundJobExternalState.Running => BackgroundJobExternalResult.Running<Outcome>(),
            BackgroundJobExternalState.Unknown => BackgroundJobExternalResult.Unknown<Outcome>(),
            BackgroundJobExternalState.NeverStarted => BackgroundJobExternalResult.NeverStarted<Outcome>(),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };
        using var boundary = BackgroundJobExecutionAdmission.EnterExternal();
        var retained = BackgroundJobRetainedExternalObservation.Capture(Context(), Fingerprint, report);
        retained.State.Should().Be(state);
        retained.IsCompleted.Should().BeFalse();
        retained.OutcomeJson.Should().BeNull();
        retained.FailurePolicy.Should().BeNull();
        retained.QuiescenceId.Should().BeNull();
        retained.QuiescenceKind.Should().BeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Foreign_operation_or_uncounted_attempt_cannot_label_retained_facts(bool wrongAttempt)
    {
        // Array zero-initialization intentionally models an uninitialized Vogen
        // value without teaching a prohibited default-construction call site.
        var context = wrongAttempt ? Context() with { AttemptId = (new BackgroundJobAttemptId[1])[0] }
            : Context() with { OperationId = Guid.NewGuid() };
        using var boundary = BackgroundJobExecutionAdmission.EnterExternal();
        var capture = () => BackgroundJobRetainedExternalObservation.Capture(context, Fingerprint,
            BackgroundJobExternalResult.Unknown<Outcome>());
        capture.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Observation_capture_does_not_create_an_external_boundary_or_grant_itself_entry()
    {
        var capture = () => BackgroundJobRetainedExternalObservation.Capture(Context(), Fingerprint,
            BackgroundJobExternalResult.Unknown<Outcome>());
        capture.Should().Throw<InvalidOperationException>();
    }

    private static BackgroundJobExternalContext Context()
    {
        var job = BackgroundJobId.From(Artifact);
        return new(job, BackgroundJobAttemptId.From(Guid.CreateVersion7()),
            BackgroundJobExternalIdentity.OperationId(job, Fingerprint), DateTimeOffset.UtcNow.AddMinutes(1));
    }

    public sealed record Outcome(Guid ArtifactId) : IBackgroundJobOutcome;
}
