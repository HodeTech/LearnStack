using FluentAssertions;
using LearnStack.Infrastructure.Idempotency;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Time;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Idempotency;

/// <summary>
/// The claim protocol of <see cref="InMemoryIdempotencyStore"/>, per
/// <see href="../../../../../docs/decisions/0037-idempotency-key-contract.md">ADR-0037</see>.
/// </summary>
/// <remarks>
/// These are the tests the store shipped without. Every property here is one an
/// endpoint carrying <c>[Idempotent]</c> depends on for correctness, and none of
/// them was observable through the HTTP-level suite: the timeout, the retention
/// window, the fencing token and the ceilings are all reachable only by moving
/// the clock.
/// </remarks>
public sealed class InMemoryIdempotencyStoreTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 8, 20, 9, 0, 0, TimeSpan.Zero);

    private static readonly Guid Tenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000a");
    private static readonly Guid OtherTenant = Guid.Parse("018f4d40-0000-7000-8000-00000000000b");

    private const string Key = "01HXIDEMPOTENT0001";
    private const string Fingerprint = "fingerprint-a";

    [Fact]
    public async Task A_Fresh_Key_Is_Acquired_With_A_Token()
    {
        var (store, _) = NewStore();

        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        claim.Outcome.Should().Be(IdempotencyClaim.Acquired);
        claim.Token.Should().NotBe(Guid.Empty, "the caller needs it back to record or release");
        claim.Stored.Should().BeNull();
    }

    [Fact]
    public async Task A_Second_Claim_On_A_Running_Operation_Is_InFlight()
    {
        var (store, _) = NewStore();
        await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        var second = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        second.Outcome.Should().Be(IdempotencyClaim.InFlight);
        second.Token.Should().Be(Guid.Empty, "a caller that did not win must not be able to fence");
    }

    [Fact]
    public async Task A_Completed_Key_Replays_Its_Response()
    {
        var (store, _) = NewStore();
        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        await store.CompleteAsync(Tenant, Key, claim.Token, Response(201), default);

        var replay = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        replay.Outcome.Should().Be(IdempotencyClaim.Completed);
        replay.Stored!.StatusCode.Should().Be(201);
    }

    [Fact]
    public async Task An_Abandoned_Key_Is_Free_Again()
    {
        var (store, _) = NewStore();
        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        await store.AbandonAsync(Tenant, Key, claim.Token, default);

        (await store.TryClaimAsync(Tenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Acquired);
    }

    [Fact]
    public async Task Two_Tenants_Do_Not_Share_A_Key_Space()
    {
        var (store, _) = NewStore();
        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        await store.CompleteAsync(Tenant, Key, claim.Token, Response(200), default);

        (await store.TryClaimAsync(OtherTenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Acquired,
                "the key is client-chosen, so two tenants will eventually pick the same one");
    }

    [Fact]
    public async Task An_Unscoped_Tenant_Is_Refused()
    {
        var (store, _) = NewStore();

        var claim = async () => await store.TryClaimAsync(Guid.Empty, Key, Fingerprint, default);

        await claim.Should().ThrowAsync<ArgumentException>(
            "Guid.Empty is not a tenant — it is a call site that forgot to scope");
    }

    // ---- fingerprint -------------------------------------------------------

    [Fact]
    public async Task The_Same_Key_For_A_Different_Request_Is_Mismatched_Not_Replayed()
    {
        // The key is client-chosen. Replaying here would answer a question the
        // caller did not ask — the classic shape being a client that reused a
        // key after editing the amount and was told the edit succeeded.
        var (store, _) = NewStore();
        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        await store.CompleteAsync(Tenant, Key, claim.Token, Response(200), default);

        var reused = await store.TryClaimAsync(Tenant, Key, "fingerprint-b", default);

        reused.Outcome.Should().Be(IdempotencyClaim.Mismatched);
        reused.Stored.Should().BeNull("a mismatched caller is told, not shown");
    }

    [Fact]
    public async Task A_Different_Request_Colliding_With_A_Running_One_Is_Mismatched()
    {
        var (store, _) = NewStore();
        await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        (await store.TryClaimAsync(Tenant, Key, "fingerprint-b", default))
            .Outcome.Should().Be(IdempotencyClaim.Mismatched);
    }

    // ---- expiry ------------------------------------------------------------

    [Fact]
    public async Task A_Claim_Past_The_Timeout_Is_Taken_Over()
    {
        // A process that died mid-operation must not hold the key until the
        // retention window closes.
        var (store, clock) = NewStore();
        await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        clock.Advance(InMemoryIdempotencyStore.ClaimTimeout);

        (await store.TryClaimAsync(Tenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Acquired);
    }

    [Fact]
    public async Task A_Claim_Just_Inside_The_Timeout_Still_Holds()
    {
        var (store, clock) = NewStore();
        await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        clock.Advance(InMemoryIdempotencyStore.ClaimTimeout - TimeSpan.FromSeconds(1));

        (await store.TryClaimAsync(Tenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.InFlight);
    }

    [Fact]
    public async Task A_Completed_Record_Outlives_The_Claim_Timeout()
    {
        var (store, clock) = NewStore();
        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        await store.CompleteAsync(Tenant, Key, claim.Token, Response(200), default);

        clock.Advance(InMemoryIdempotencyStore.ClaimTimeout * 2);

        (await store.TryClaimAsync(Tenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Completed,
                "the claim timeout bounds an unfinished attempt, not a finished one");
    }

    [Fact]
    public async Task A_Completed_Record_Expires_After_The_Retention_Window()
    {
        var (store, clock) = NewStore();
        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        await store.CompleteAsync(Tenant, Key, claim.Token, Response(200), default);

        clock.Advance(InMemoryIdempotencyStore.Retention);

        (await store.TryClaimAsync(Tenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Acquired);
    }

    // ---- fencing -----------------------------------------------------------

    [Fact]
    public async Task A_Superseded_Attempt_Cannot_Overwrite_The_Record_Of_The_One_That_Replaced_It()
    {
        // Attempt 1 overruns the claim timeout, attempt 2 takes the key and
        // finishes. Attempt 1 then returns. Without fencing, the older answer
        // silently replaces the newer one for the rest of the window.
        var (store, clock) = NewStore();
        var first = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        clock.Advance(InMemoryIdempotencyStore.ClaimTimeout);
        var second = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        second.Outcome.Should().Be(IdempotencyClaim.Acquired);
        await store.CompleteAsync(Tenant, Key, second.Token, Response(201), default);

        await store.CompleteAsync(Tenant, Key, first.Token, Response(500), default);

        var replay = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        replay.Stored!.StatusCode.Should().Be(201, "the winner's response is the one that stands");
    }

    [Fact]
    public async Task A_Superseded_Attempt_Cannot_Release_The_Successors_Claim()
    {
        // The mirror image: attempt 1's cleanup deleting attempt 2's live claim
        // would let a third request run the operation alongside attempt 2.
        var (store, clock) = NewStore();
        var first = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        clock.Advance(InMemoryIdempotencyStore.ClaimTimeout);
        var second = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        await store.AbandonAsync(Tenant, Key, first.Token, default);

        (await store.TryClaimAsync(Tenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.InFlight,
                "the key still belongs to the attempt that is running");
        second.Token.Should().NotBe(first.Token);
    }

    [Fact]
    public async Task A_Stale_Token_Cannot_Delete_A_Completed_Record()
    {
        var (store, clock) = NewStore();
        var first = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        clock.Advance(InMemoryIdempotencyStore.ClaimTimeout);
        var second = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        await store.CompleteAsync(Tenant, Key, second.Token, Response(200), default);

        await store.AbandonAsync(Tenant, Key, first.Token, default);

        (await store.TryClaimAsync(Tenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Completed);
    }

    // ---- ceilings ----------------------------------------------------------

    [Fact]
    public async Task The_Ceiling_Never_Evicts_A_Live_Claim()
    {
        // Evicting a live claim releases a key whose operation is still
        // running, so the next retry executes it again — the exact duplication
        // the store exists to prevent. A bounded overshoot is the cheaper side.
        var (store, clock) = NewStore();
        var claimed = new List<string>();

        for (var i = 0; i <= InMemoryIdempotencyStore.MaxEntriesPerTenant + 50; i++)
        {
            var key = $"live-{i:D5}";
            (await store.TryClaimAsync(Tenant, key, Fingerprint, default))
                .Outcome.Should().Be(IdempotencyClaim.Acquired);
            claimed.Add(key);
        }

        // Arm the sweep — the ceilings are enforced inside it — without letting
        // any of the claims above reach the claim timeout, so an eviction here
        // could only be the ceiling's doing.
        clock.Advance(InMemoryIdempotencyStore.SweepInterval);
        await store.TryClaimAsync(Tenant, "trigger-the-sweep", Fingerprint, default);

        foreach (var key in claimed)
        {
            (await store.TryClaimAsync(Tenant, key, Fingerprint, default))
                .Outcome.Should().Be(IdempotencyClaim.InFlight,
                    $"the claim on {key} is still running and must not have been evicted");
        }
    }

    [Fact]
    public async Task One_Tenants_Flood_Does_Not_Evict_Another_Tenants_Record()
    {
        // The global ceiling is a shared resource. Without a per-tenant bound,
        // one tenant minting keys in a loop revokes every other tenant's
        // records, and their retries then re-run completed operations.
        var (store, clock) = NewStore();
        var victim = await store.TryClaimAsync(OtherTenant, Key, Fingerprint, default);
        await store.CompleteAsync(OtherTenant, Key, victim.Token, Response(200), default);

        for (var i = 0; i < InMemoryIdempotencyStore.MaxEntriesPerTenant * 2; i++)
        {
            var key = $"flood-{i:D6}";
            var claim = await store.TryClaimAsync(Tenant, key, Fingerprint, default);
            await store.CompleteAsync(Tenant, key, claim.Token, Response(200), default);
            clock.Advance(InMemoryIdempotencyStore.SweepInterval);
        }

        (await store.TryClaimAsync(OtherTenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Completed,
                "the flooding tenant's keys are evicted against its own allowance");
    }

    [Fact]
    public async Task A_Flooding_Tenant_Is_Held_To_Its_Own_Allowance()
    {
        var (store, clock) = NewStore();

        for (var i = 0; i < InMemoryIdempotencyStore.MaxEntriesPerTenant + 200; i++)
        {
            var key = $"flood-{i:D6}";
            var claim = await store.TryClaimAsync(Tenant, key, Fingerprint, default);
            await store.CompleteAsync(Tenant, key, claim.Token, Response(200), default);
            clock.Advance(InMemoryIdempotencyStore.SweepInterval);
        }

        (await store.TryClaimAsync(Tenant, "flood-000000", Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Acquired,
                "the oldest completed records are the ones the allowance drops");
    }

    // ---- the race ----------------------------------------------------------

    [Fact]
    public async Task A_Sweep_Never_Destroys_A_Claim_Another_Thread_Just_Won()
    {
        // The defect this test exists for: the expiry sweep observed an entry,
        // then removed it BY KEY. Between those two steps another thread could
        // take the key over and install a live claim, which the sweep then
        // deleted — and the next caller, finding the key absent, was told to run
        // the operation. Two callers, one key, one tenant, both running.
        //
        // Every key below starts out holding an EXPIRED claim, so the sweep has
        // something to collect on every pass. The clock advances one sweep
        // interval per round and the run is bounded well under ClaimTimeout, so
        // no claim taken during the run can legitimately expire. Each key may
        // therefore be acquired exactly once: the takeover. A second acquire is
        // the bug.
        const int keys = 8;
        const int threads = 16;
        const int rounds = 200;

        var clock = new AtomicClock(Origin);
        var store = new InMemoryIdempotencyStore(clock);

        for (var i = 0; i < keys; i++)
        {
            await store.TryClaimAsync(Tenant, $"raced-{i}", Fingerprint, default);
        }

        clock.Advance(InMemoryIdempotencyStore.ClaimTimeout);

        var acquisitions = new int[keys];
        using var start = new Barrier(threads);

        // Dedicated threads, not pool tasks: the claims complete synchronously,
        // so pool tasks would largely run one after another and the interleaving
        // this test is looking for would never happen.
        var workers = Enumerable.Range(0, threads).Select(worker =>
            Task.Factory.StartNew(
                () =>
                {
                    start.SignalAndWait();

                    for (var round = 0; round < rounds; round++)
                    {
                        // One driver, so the total advance stays bounded.
                        if (worker == 0)
                        {
                            clock.Advance(InMemoryIdempotencyStore.SweepInterval);
                        }

                        for (var i = 0; i < keys; i++)
                        {
                            var claim = store
                                .TryClaimAsync(Tenant, $"raced-{i}", Fingerprint, default)
                                .GetAwaiter().GetResult();

                            if (claim.Outcome == IdempotencyClaim.Acquired)
                            {
                                Interlocked.Increment(ref acquisitions[i]);
                            }
                        }
                    }
                },
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default)).ToArray();

        await Task.WhenAll(workers);

        acquisitions.Should().OnlyContain(count => count == 1,
            "each key was acquired by exactly one caller; a second acquire means a live "
            + "claim was destroyed and the operation would have run twice");
    }

    private static (InMemoryIdempotencyStore Store, FixedClock Clock) NewStore()
    {
        var clock = new FixedClock(Origin);
        return (new InMemoryIdempotencyStore(clock), clock);
    }

    private static IdempotentResponse Response(int status) =>
        new(status, "application/json", new Dictionary<string, IReadOnlyList<string>>(), [1, 2, 3]);

    /// <summary>
    /// A clock the race test can advance from one thread while others read it.
    /// <see cref="FixedClock"/> stores a <see cref="DateTimeOffset"/> in a plain
    /// field, and reads of one are not atomic — a torn value would make the test
    /// fail for a reason that has nothing to do with the store.
    /// </summary>
    private sealed class AtomicClock(DateTimeOffset origin) : IClock
    {
        private long _ticks = origin.UtcTicks;

        public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

        public void Advance(TimeSpan delta) => Interlocked.Add(ref _ticks, delta.Ticks);
    }
}
