using LearnStack.SharedKernel.Identity;

namespace LearnStack.Infrastructure.Identity.ProviderEvents;

/// <summary>One bounded forward slice and a separate fair retry lane. Neither declares gap-free source capture.</summary>
public sealed class ProviderEventScanner(IIdentityEventProvider provider, ProviderEventIntakeSource source)
{
    public async Task<bool> SweepPageAsync(CancellationToken token = default)
    {
        var state = await source.BeginSweepAsync(token).ConfigureAwait(false);
        var progress = await ReadAndAcceptAsync(new(state.From!.Value, state.To!.Value, state.Offset), token).ConfigureAwait(false);
        if (!await source.AdvanceSweepAsync(progress.Request, progress.Count, progress.Finished, token).ConfigureAwait(false))
            return false; // Another process advanced the same durable source. Re-read rather than invent progress.
        return progress.Finished;
    }

    public async Task<IdentityEventRead?> RecentPageAsync(IdentityEventRead page, CancellationToken token = default)
    {
        var progress = await ReadAndAcceptAsync(page, token).ConfigureAwait(false);
        return progress.Finished ? null : new(page.From, page.To, checked(page.Offset + progress.Count));
    }

    public async Task RetryOneAsync(CancellationToken token = default)
    {
        var problem = await source.TakeProblemAsync(token).ConfigureAwait(false);
        if (problem is null) return;
        if (problem is { Reason: "fanout_exceeded", EventId: { } id })
        {
            // This is a replay of a durably observed minimized fact, not a new provider ACK.
            var fact = await source.ReadPendingFactAsync(id, token).ConfigureAwait(false);
            if (fact is not null)
                await source.AcceptAsync(fact.EventId, fact.OccurredAt, fact.Kind, fact.SubjectKey, token).ConfigureAwait(false);
            return;
        }
        if (problem.Offset is not { } offset) throw new InvalidOperationException("Provider retry locator is invalid.");
        var page = new IdentityEventRead(problem.From, problem.To, offset, 1);
        var result = await provider.ReadAsync(page, token).ConfigureAwait(false);
        if (result.Issue is not null || result.Items.Any(item => item.Disposition == IdentityEventItemDisposition.Malformed)) return;
        foreach (var item in result.Items)
            await AcceptItemAsync(item, token).ConfigureAwait(false);
        // The original unreadable position has no normalized immutable identity.
        // A missing or valid replacement at this offset cannot prove recovery of
        // that fact. Keep its coverage problem unresolved through retirement;
        // useful retry facts still receive their independent acceptance ACK.
    }

    private async Task<Progress> ReadAndAcceptAsync(IdentityEventRead original, CancellationToken token)
    {
        var page = original;
        IdentityEventPage result;
        while (true)
        {
            result = await provider.ReadAsync(page, token).ConfigureAwait(false);
            if (result.Issue is null) break;
            if (page.Maximum == 1)
            {
                await source.RecordPageProblemAsync(page, result.Issue.Value, token).ConfigureAwait(false);
                return new(page, 1, false);
            }
            page = new(page.From, page.To, page.Offset, Math.Max(1, page.Maximum / 2));
        }
        if (result.Items.Length > page.Maximum) throw new InvalidOperationException("Provider page exceeds its requested bound.");
        for (var index = 0; index < result.Items.Length; index++)
        {
            var item = result.Items[index];
            if (item.Disposition == IdentityEventItemDisposition.Malformed)
                await source.RecordPageProblemAsync(new(page.From, page.To, checked(page.Offset + index), 1), IdentityEventPageIssue.Malformed, token).ConfigureAwait(false);
            else await AcceptItemAsync(item, token).ConfigureAwait(false);
        }
        return new(page, result.Items.Length, result.Items.Length < page.Maximum);
    }

    private async Task AcceptItemAsync(IdentityEventReadItem item, CancellationToken token)
    {
        if (item.Disposition == IdentityEventItemDisposition.Unselected) return;
        if (item is not { Disposition: IdentityEventItemDisposition.Fact, Fact: { } fact })
            throw new InvalidOperationException("Provider item is not normalized.");
        // An unaccepted overflow/collision remains durable and visible; it does not block siblings.
        await source.AcceptAsync(fact.SourceEventId, fact.OccurredAt, IdentityEventKindCodes.Code(fact.Kind), fact.SubjectKey, token).ConfigureAwait(false);
    }

    private sealed record Progress(IdentityEventRead Request, int Count, bool Finished);
}
