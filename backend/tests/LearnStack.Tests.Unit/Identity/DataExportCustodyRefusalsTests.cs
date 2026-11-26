using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.DataRequests;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

/// <summary>
/// The custody refusal memory behind the critical-once log of the data export runbook: one critical report per
/// artifact and key label, repeats as warnings, and a bounded memory that forgets rather than grows.
/// </summary>
public sealed class DataExportCustodyRefusalsTests
{
    [Fact]
    public void The_first_refusal_of_an_artifact_under_a_label_is_reported_once_and_another_pair_is_its_own_first()
    {
        var refusals = new DataExportCustodyRefusals();
        var artifact = Guid.CreateVersion7();

        refusals.IsFirst(artifact, "v1").Should().BeTrue();
        refusals.IsFirst(artifact, "v1").Should().BeFalse("every further chunk of the same artifact and label is a repeat");
        refusals.IsFirst(artifact, "v2").Should().BeTrue("another key label is another custody problem");
        refusals.IsFirst(Guid.CreateVersion7(), "v1").Should().BeTrue("another artifact is reported in its own right");
    }

    [Fact]
    public void The_memory_is_bounded_and_forgets_everything_when_a_new_pair_would_pass_the_bound()
    {
        var refusals = new DataExportCustodyRefusals();
        var first = Guid.CreateVersion7();
        refusals.IsFirst(first, "v1").Should().BeTrue();
        for (var index = 1; index < DataExportCustodyRefusals.Capacity; index++)
        {
            refusals.IsFirst(Guid.CreateVersion7(), "v1").Should().BeTrue();
        }

        refusals.IsFirst(first, "v1").Should().BeFalse("a full memory still knows what it holds");
        refusals.IsFirst(Guid.CreateVersion7(), "v1").Should().BeTrue("a new pair past the bound clears the memory and is remembered");
        refusals.IsFirst(first, "v1").Should().BeTrue("a cleared memory reports an old refusal at critical severity again");
    }
}
