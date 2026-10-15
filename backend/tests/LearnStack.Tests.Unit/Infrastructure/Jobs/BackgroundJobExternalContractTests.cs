using FluentAssertions;
using LearnStack.SharedKernel.Jobs;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobExternalContractTests
{
    private static readonly Guid Sample = Guid.Parse("019712ac-1234-7000-8000-0000000000ab");

    [Fact]
    public void Indeterminate_reports_do_not_manufacture_outcomes_or_quiescence()
    {
        foreach (var report in new[] { BackgroundJobExternalResult.Running<Outcome>(),
            BackgroundJobExternalResult.Unknown<Outcome>(), BackgroundJobExternalResult.NeverStarted<Outcome>() })
        {
            report.Outcome.Should().BeNull();
            report.Quiescence.Should().BeNull();
        }
        typeof(BackgroundJobExternalResult<Outcome>).GetConstructors().Should().BeEmpty();
    }

    [Theory]
    [InlineData(BackgroundJobExternalState.Running)]
    [InlineData(BackgroundJobExternalState.Unknown)]
    [InlineData(BackgroundJobExternalState.NeverStarted)]
    [InlineData((BackgroundJobExternalState)99)]
    public void Unknown_or_running_cannot_be_reported_as_completed(BackgroundJobExternalState state)
    {
        var action = () => BackgroundJobExternalResult.Completed(state, new Outcome(Sample),
            new BackgroundJobQuiescenceEvidence(Sample, BackgroundJobQuiescenceKind.ProviderFenced));
        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(BackgroundJobExternalState.Succeeded)]
    [InlineData(BackgroundJobExternalState.Failed)]
    [InlineData(BackgroundJobExternalState.Cancelled)]
    public void Known_completion_requires_outcome_and_positive_evidence(BackgroundJobExternalState state)
    {
        var evidence = new BackgroundJobQuiescenceEvidence(Sample, BackgroundJobQuiescenceKind.ExecutionTerminated);
        var outcome = new Outcome(Sample);
        var report = BackgroundJobExternalResult.Completed(state, outcome, evidence);
        report.State.Should().Be(state);
        report.Outcome.Should().BeSameAs(outcome);
        report.Quiescence.Should().BeSameAs(evidence);
        var missingEvidence = () => BackgroundJobExternalResult.Completed(state, outcome, null!);
        missingEvidence.Should().Throw<ArgumentNullException>();
        var missingOutcome = () => BackgroundJobExternalResult.Completed<Outcome>(state, null!, evidence);
        missingOutcome.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Finalization_can_apply_known_failure_after_prepare_without_fabricating_a_provider_outcome()
    {
        var plan = new Plan(Sample);
        var failure = new BackgroundJobFinalization<Plan, Outcome>(plan, null,
            BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.NormalAttemptsExhausted);
        failure.Plan.Should().BeSameAs(plan);
        failure.Outcome.Should().BeNull();
        failure.Status.Should().Be(BackgroundJobFinalizationStatus.Failed);
        var success = new BackgroundJobFinalization<Plan, Outcome>(plan, new(Sample),
            BackgroundJobFinalizationStatus.Succeeded, BackgroundJobFinalizationReason.None);
        success.Status.Should().Be(BackgroundJobFinalizationStatus.Succeeded);
        var noSuccessOutcome = () => new BackgroundJobFinalization<Plan, Outcome>(plan, null,
            BackgroundJobFinalizationStatus.Succeeded, BackgroundJobFinalizationReason.None);
        noSuccessOutcome.Should().Throw<ArgumentException>();
        var noFailureReason = () => new BackgroundJobFinalization<Plan, Outcome>(plan, null,
            BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.None);
        noFailureReason.Should().Throw<ArgumentException>();
        var noProviderOutcome = () => new BackgroundJobFinalization<Plan, Outcome>(plan, null,
            BackgroundJobFinalizationStatus.Failed, BackgroundJobFinalizationReason.ExternalFailed);
        noProviderOutcome.Should().Throw<ArgumentException>();
    }

    public sealed record Plan(Guid ArtifactId) : IBackgroundJobPlan;
    public sealed record Outcome(Guid ArtifactId) : IBackgroundJobOutcome;
}
