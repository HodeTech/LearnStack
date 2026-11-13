using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Federation;
using LearnStack.Modules.Identity.Application.Federation;
using MediatR;
using Mono.Cecil;
using Xunit;

namespace LearnStack.Tests.Architecture;

public sealed partial class AuthenticatedHttpSurfaceTests
{
    /// <summary>
    /// Step 2 review C-9: the federation hand-off guard can fail. Planted actions that call the runner twice, call it
    /// alongside <see cref="ISender"/>, or never call it are refused; the one-runner-call shape, async or not, is admitted.
    /// </summary>
    [Fact]
    public void The_federation_handoff_guard_refuses_a_second_runner_call_a_sender_or_no_runner_call()
    {
        using var compiled = ModuleDefinition.ReadModule(typeof(FederationHandoffProbe).Assembly.Location);
        var probe = compiled.GetType(typeof(FederationHandoffProbe).FullName);
        bool Admitted(string name) => FederationHandoff(probe, probe.Methods.Single(method => method.Name == name));
        Admitted(nameof(FederationHandoffProbe.Once)).Should().BeTrue("one runner call and no sender is the sanctioned shape");
        Admitted(nameof(FederationHandoffProbe.OnceAsync)).Should().BeTrue("an async action's state machine is part of the action");
        Admitted(nameof(FederationHandoffProbe.Twice)).Should().BeFalse("two runner calls are two operations");
        Admitted(nameof(FederationHandoffProbe.WithSender)).Should().BeFalse("a controller never dispatches through ISender");
        Admitted(nameof(FederationHandoffProbe.SenderInAsync)).Should().BeFalse("a sender inside the state machine is still a sender");
        Admitted(nameof(FederationHandoffProbe.None)).Should().BeFalse("an action that never reaches the runner is not a hand-off");
    }
}

/// <summary>Planted federation actions for the hand-off guard; never registered or routed.</summary>
internal sealed class FederationHandoffProbe(FederationOperationRunner runner, ISender sender)
{
    internal Task<LearnStack.SharedKernel.Results.Result<FederationRun>> Once() => runner.EnableAsync(1, CancellationToken.None);

    internal async Task<object> OnceAsync() => await runner.DisableAsync(1, CancellationToken.None);

    internal async Task<object> Twice()
    {
        await runner.EnableAsync(1, CancellationToken.None);
        return await runner.DisableAsync(2, CancellationToken.None);
    }

    internal async Task<object> WithSender()
    {
        await sender.Send(new EnableFederationCommand(1));
        return await runner.EnableAsync(1, CancellationToken.None);
    }

    internal async Task<object> SenderInAsync() => await sender.Send(new EnableFederationCommand(1));

    internal static Task<object> None() => Task.FromResult<object>(0);
}
