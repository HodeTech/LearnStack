using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Bindings;
using LearnStack.Modules.Identity.Application.Contracts.Entry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentityBootstrapTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uncertain_attempt_COMMIT_never_starts_User_creation_or_refunds_a_landed_debit(bool dropRequest)
    {
        // Pass authority qualification and the initial missing-binding lookup.
        // The third physical owner's COMMIT is the durable attempt, before any
        // business/audit owner exists. A dropped response and an unsent request
        // must have the same public refusal despite different database results.
        await using var proxy = new CommitResponseDroppingProxy(_database.AppConnectionString,
            commitsToPass: 2, dropCommitRequest: dropRequest);
        await using (var provider = BuildProvider(proxy.ConnectionString, Registration, configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IIdentitySelfEntryAdmission>(new FixtureSelfAdmission(true)))))
        {
            await QualifyAsync(provider);

            var result = await BootstrapAsync(provider, Subject).WaitAsync(TimeSpan.FromSeconds(15));

            result.Refusal.Should().Be(IdentityCreationRefusal.Unavailable);
            result.Binding.Actor.Should().BeNull();
        }
        if (dropRequest) proxy.DroppedCommitRequests.Should().Be(1);
        else proxy.DroppedCommitResponses.Should().Be(1);
        (await CreationCountersAsync()).Should().Be(dropRequest ? (0, 0, 0L) : (1, 1, 0L));
        (await CountsAsync()).Should().Be((0L, 0L),
            "uncertain attempt durability cannot declare a User creation intent or return an association");

        // A deliberate new attempt observes persisted state. It neither resets
        // nor refunds the earlier debit merely because the caller saw 503.
        await using var clean = SelfProvider();
        await QualifyAsync(clean);
        (await BootstrapAsync(clean, Subject)).Binding.Outcome.Should().Be(IdentityBindingOutcome.Created);
        (await CreationCountersAsync()).Should().Be(dropRequest ? (1, 1, 1L) : (2, 2, 1L));
        (await CountsAsync()).Should().Be((1L, 1L));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Uncertain_self_creation_COMMIT_withholds_the_actor_and_retains_its_separate_attempt_debit(bool dropRequest)
    {
        // Qualification, initial lookup and the independent attempt debit have
        // acknowledged COMMITs. Fault only the subsequent User + central MUST
        // transaction, exercising the real self-bootstrap split-owner path.
        await using var proxy = new CommitResponseDroppingProxy(_database.AppConnectionString,
            commitsToPass: 3, dropCommitRequest: dropRequest);
        await using (var provider = BuildProvider(proxy.ConnectionString, Registration, configure: services =>
            services.Replace(ServiceDescriptor.Singleton<IIdentitySelfEntryAdmission>(new FixtureSelfAdmission(true)))))
        {
            await QualifyAsync(provider);

            var result = await BootstrapAsync(provider, Subject).WaitAsync(TimeSpan.FromSeconds(15));

            result.Binding.Outcome.Should().Be(IdentityBindingOutcome.Indeterminate);
            result.Binding.Actor.Should().BeNull("an unknown business COMMIT cannot publish an association");
        }
        if (dropRequest) proxy.DroppedCommitRequests.Should().Be(1);
        else proxy.DroppedCommitResponses.Should().Be(1);
        (await CreationCountersAsync()).Should().Be(dropRequest ? (1, 1, 0L) : (1, 1, 1L));
        (await CountsAsync()).Should().Be(dropRequest ? (0L, 1L) : (1L, 2L));
        (await AuditRowsAsync()).Should().ContainSingle(row => row.Outcome == "indeterminate");

        // A landed User is found without another debit. An unsent business
        // COMMIT leaves no binding, so a deliberate retry spends another attempt
        // before creating its one User and success witness.
        await using var clean = SelfProvider();
        await QualifyAsync(clean);
        (await BootstrapAsync(clean, Subject)).Binding.Outcome.Should().Be(
            dropRequest ? IdentityBindingOutcome.Created : IdentityBindingOutcome.Existing);
        (await CreationCountersAsync()).Should().Be(dropRequest ? (2, 2, 1L) : (1, 1, 1L));
        (await CountsAsync()).Should().Be((1L, 2L));
    }
}
