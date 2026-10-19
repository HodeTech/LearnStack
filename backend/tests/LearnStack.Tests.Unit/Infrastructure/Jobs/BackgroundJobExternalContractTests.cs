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
            report.FailurePolicy.Should().BeNull();
        }
        typeof(BackgroundJobExternalResult<Outcome>).GetConstructors().Should().BeEmpty();
    }

    [Fact]
    public void Report_factories_expose_no_generic_completed_or_implicit_failure_policy()
    {
        var methods = typeof(BackgroundJobExternalResult).GetMethods(System.Reflection.BindingFlags.Public
            | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly);
        methods.Select(method => method.Name).Should().BeEquivalentTo(
            "Running", "Unknown", "NeverStarted", "Succeeded", "Failed", "Cancelled");
        foreach (var failure in methods.Where(method => method.Name is "Failed" or "Cancelled"))
        {
            var policy = failure.GetParameters().Single(parameter => parameter.ParameterType == typeof(BackgroundJobExternalFailurePolicy));
            policy.IsOptional.Should().BeFalse();
            policy.HasDefaultValue.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(BackgroundJobExternalState.Succeeded)]
    [InlineData(BackgroundJobExternalState.Failed)]
    [InlineData(BackgroundJobExternalState.Cancelled)]
    public void Known_completion_requires_outcome_and_positive_evidence(BackgroundJobExternalState state)
    {
        var evidence = new BackgroundJobQuiescenceEvidence(Sample, BackgroundJobQuiescenceKind.ExecutionTerminated);
        var outcome = new Outcome(Sample);
        var report = Complete(state, outcome, evidence);
        report.State.Should().Be(state);
        report.Outcome.Should().BeSameAs(outcome);
        report.Quiescence.Should().BeSameAs(evidence);
        var missingEvidence = () => Complete(state, outcome, null!);
        missingEvidence.Should().Throw<ArgumentNullException>();
        var missingOutcome = () => Complete(state, null!, evidence);
        missingOutcome.Should().Throw<ArgumentNullException>();
    }

    [Theory]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.Permanent)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientRepeatable)]
    [InlineData(BackgroundJobExternalState.Failed, BackgroundJobExternalFailurePolicy.TransientNotRepeatable)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientRepeatable)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.TransientNotRepeatable)]
    public void Completed_failure_preserves_its_explicit_policy_without_state_conversion(
        BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy policy)
    {
        var outcome = new Outcome(Sample);
        var evidence = new BackgroundJobQuiescenceEvidence(Sample, BackgroundJobQuiescenceKind.ProviderFenced);
        var result = state == BackgroundJobExternalState.Failed
            ? BackgroundJobExternalResult.Failed(outcome, evidence, policy)
            : BackgroundJobExternalResult.Cancelled(outcome, evidence, policy);
        result.State.Should().Be(state);
        result.FailurePolicy.Should().Be(policy);
        result.Outcome.Should().BeSameAs(outcome);
        result.Quiescence.Should().BeSameAs(evidence);
        BackgroundJobExternalResult.Succeeded(outcome, evidence).FailurePolicy.Should().BeNull();
    }

    [Theory]
    [InlineData(BackgroundJobExternalState.Failed, (BackgroundJobExternalFailurePolicy)0)]
    [InlineData(BackgroundJobExternalState.Failed, (BackgroundJobExternalFailurePolicy)99)]
    [InlineData(BackgroundJobExternalState.Cancelled, (BackgroundJobExternalFailurePolicy)0)]
    [InlineData(BackgroundJobExternalState.Cancelled, (BackgroundJobExternalFailurePolicy)99)]
    [InlineData(BackgroundJobExternalState.Cancelled, BackgroundJobExternalFailurePolicy.Permanent)]
    public void Omitted_undefined_and_permanent_cancellation_policy_are_refused(
        BackgroundJobExternalState state, BackgroundJobExternalFailurePolicy policy)
    {
        var outcome = new Outcome(Sample);
        var evidence = new BackgroundJobQuiescenceEvidence(Sample, BackgroundJobQuiescenceKind.ProviderFenced);
        var report = () => state == BackgroundJobExternalState.Failed
            ? BackgroundJobExternalResult.Failed(outcome, evidence, policy)
            : BackgroundJobExternalResult.Cancelled(outcome, evidence, policy);
        report.Should().Throw<ArgumentException>();
    }

    private static BackgroundJobExternalResult<Outcome> Complete(BackgroundJobExternalState state,
        Outcome outcome, BackgroundJobQuiescenceEvidence evidence) => state switch
        {
            BackgroundJobExternalState.Succeeded => BackgroundJobExternalResult.Succeeded(outcome, evidence),
            BackgroundJobExternalState.Failed => BackgroundJobExternalResult.Failed(outcome, evidence,
                BackgroundJobExternalFailurePolicy.Permanent),
            BackgroundJobExternalState.Cancelled => BackgroundJobExternalResult.Cancelled(outcome, evidence,
                BackgroundJobExternalFailurePolicy.TransientNotRepeatable),
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

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
