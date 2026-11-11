using FluentAssertions;
using LearnStack.Infrastructure.Identity.ProviderEvents;
using LearnStack.SharedKernel.Identity;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class ProviderEventStorageTests
{
    [Fact]
    public async Task Oversized_singleton_is_durable_and_later_history_advances_across_source_restart_without_a_false_complete_scan()
    {
        await using var source = await QualifiedSourceAsync();
        var id = Guid.NewGuid();
        var at = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds());
        var provider = new PoisonPageProvider(new(id, at, IdentityProviderEventKind.LoginFailure, null));
        var scanner = new ProviderEventScanner(provider, source);
        var first = await source.BeginSweepAsync();
        first.From.Should().NotBeNull(); first.To.Should().NotBeNull();
        (await scanner.SweepPageAsync()).Should().BeFalse();
        var saved = await source.ReadScanAsync();
        saved.Offset.Should().Be(first.Offset + 1);
        provider.Requests.Select(read => read.Maximum).Should().ContainInOrder(100, 50, 25, 12, 6, 3, 1);
        await using var restarted = await QualifiedSourceAsync();
        (await restarted.ReadScanAsync()).Offset.Should().Be(saved.Offset);
        (await new ProviderEventScanner(provider, restarted).SweepPageAsync()).Should().BeTrue();
        await using var machine = await MachineAsync();
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_receipts WHERE provider_event_id=@id AND accepted_at IS NOT NULL", ("id", id))).Should().Be(1);
        (await IdentityMembershipFixture.ScalarAsync<long>(machine, null,
            "SELECT count(*) FROM public.identity_provider_event_problems WHERE reason='oversized' AND source_offset=@offset AND range_from=@from AND range_to=@to AND resolved_at IS NULL",
            ("offset", first.Offset), ("from", first.From), ("to", first.To))).Should().Be(1);
        (await restarted.ReadScanAsync()).LastCompletedAt.Should().Be(first.LastCompletedAt,
            "traversal past an unresolved source position is never a complete scan");
        (await restarted.ReadHealthAsync()).Critical.Should().BeTrue("the retained sweep's unresolved oldest position is near source expiry");
    }

    [Fact]
    public async Task A_missing_durable_problem_write_never_advances_past_an_unreadable_position()
    {
        await using var source = await QualifiedSourceAsync();
        var before = await source.BeginSweepAsync();
        var provider = new PoisonPageProvider(new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddMinutes(-1), IdentityProviderEventKind.Login, null));
        await using var owner = await OpenAsync(fixture.Postgres.MigrationConnectionString);
        await IdentityMembershipFixture.ExecuteAsync(owner, null, "REVOKE INSERT ON public.identity_provider_event_problems FROM learnstack_identity_ingress");
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new ProviderEventScanner(provider, source).SweepPageAsync());
            var after = await source.ReadScanAsync();
            after.From.Should().Be(before.From); after.To.Should().Be(before.To); after.Offset.Should().Be(before.Offset);
        }
        finally
        {
            await IdentityMembershipFixture.ExecuteAsync(owner, null, "GRANT INSERT ON public.identity_provider_event_problems TO learnstack_identity_ingress");
        }
    }

    private sealed class PoisonPageProvider(IdentityProviderEvent later) : IIdentityEventProvider
    {
        internal List<IdentityEventRead> Requests { get; } = [];
        public Task<IdentityEventSourcePolicy> ReadPolicyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new IdentityEventSourcePolicy(604800));
        public Task<IdentityEventPage> ReadAsync(IdentityEventRead request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(request.Offset == 0 ? IdentityEventPage.Unreadable(IdentityEventPageIssue.Oversized)
                : IdentityEventPage.Read(request.Offset == 1 ? [IdentityEventReadItem.Observed(later)] : []));
        }
    }
}
