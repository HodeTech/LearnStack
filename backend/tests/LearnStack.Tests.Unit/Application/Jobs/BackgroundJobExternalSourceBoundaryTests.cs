using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Tenancy;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Jobs;

public sealed class BackgroundJobExternalSourceBoundaryTests
{
    [Theory]
    [InlineData("audit_config")]
    [InlineData("standalone_audit")]
    [InlineData("tenant_flags")]
    [InlineData("killswitch")]
    public async Task External_marker_refuses_each_auxiliary_acquisition_and_fresh_owner_while_preserving_actual_cleanup(string helper)
    {
        var owner = JobApplicationSourceOwner.Create();
        using var binding = owner.Enter();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task inherited;
        using (BackgroundJobExecutionAdmission.EnterExternal())
        {
            JobApplicationSourceOwner.IsExternal.Should().BeTrue();
            AsyncExecutionDiagnostics.IsTrusted(null).Should().BeTrue();
            var acquire = () => Begin(owner, helper);
            acquire.Should().Throw<InvalidOperationException>();
            var fresh = () => JobApplicationSourceOwner.Create().Enter();
            fresh.Should().Throw<InvalidOperationException>();
            owner.CleanupConfirmed.Should().BeTrue("denial cannot consume a connection or auxiliary slot");
            inherited = Task.Run(async () =>
            {
                await release.Task;
                acquire.Should().Throw<InvalidOperationException>();
                fresh.Should().Throw<InvalidOperationException>();
                Action unrelated = JobApplicationSourceOwner.RefuseUnrelatedRead;
                unrelated.Should().Throw<InvalidOperationException>();
            });
        }
        JobApplicationSourceOwner.IsExternal.Should().BeFalse();
        var positive = Begin(owner, helper);
        positive.HoldActualTask(Task.CompletedTask);
        await positive.CleanupAsync();
        owner.CleanupConfirmed.Should().BeTrue("the same helper remains usable outside the provider boundary");
        release.SetResult();
        await inherited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static JobApplicationAuxiliaryResources Begin(JobApplicationSourceOwner owner, string helper) => helper switch
    {
        "audit_config" => owner.BeginAuditConfigLoad(),
        "standalone_audit" => owner.BeginStandaloneAudit(),
        "tenant_flags" => owner.BeginTenantFlagsLoad(),
        "killswitch" => owner.BeginKillswitchLoad(),
        _ => throw new ArgumentException("The test names one actual closed helper.", nameof(helper)),
    };
}
