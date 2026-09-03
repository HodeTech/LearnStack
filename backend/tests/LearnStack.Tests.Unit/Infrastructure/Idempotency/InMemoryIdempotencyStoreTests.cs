using FluentAssertions;
using LearnStack.Infrastructure.Idempotency;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
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

    // Typed, as of Packet 7: the port's key space is (TenantId, string), which is what
    // ADR-0037 promised when it said the raw Guid and ITenantContext.TenantId "both move
    // together". A raw Guid no longer compiles here, which is the point of the change.
    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-00000000000a"));

    private static readonly TenantId OtherTenant =
        TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-00000000000b"));

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

        // default(TenantId) does not compile — Vogen's VOG009 analyzer prohibits it — so
        // the unassigned value comes from an array element, which the analyzer cannot see
        // and the runtime leaves zeroed. That is also how one reaches production: a struct
        // field nobody assigned, a default(T) in a generic, a deserializer that skipped a
        // member. Typing the port did not remove this guard's job; it changed the shape of
        // the sentinel it has to refuse.
        var slot = new TenantId[1];
        var unassigned = slot[0];

        var claim = async () => await store.TryClaimAsync(unassigned, Key, Fingerprint, default);

        await claim.Should().ThrowAsync<ArgumentException>(
            "an unassigned id is not a tenant — it is a call site that forgot to scope");
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

    // ---- capacity ----------------------------------------------------------

    [Fact]
    public async Task A_Tenant_At_Its_Allowance_Is_Refused_A_New_Key()
    {
        // Admission, not eviction. The alternative — dropping an unexpired
        // record to make room — would let the operation that record describes
        // run a second time, so the capacity control would be quietly
        // cancelling the guarantee it exists to protect.
        var (store, clock) = await FilledToAllowanceAsync();

        (await store.TryClaimAsync(Tenant, "one-key-too-many", Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.CapacityExhausted);

        clock.Should().NotBeNull();
    }

    [Fact]
    public async Task An_Existing_Key_Is_Still_Served_At_Capacity()
    {
        // A client holding a key must never be locked out by another client's
        // flood — its retry is the request the mechanism exists to answer.
        var (store, _) = await FilledToAllowanceAsync();

        (await store.TryClaimAsync(Tenant, "filler-00000", Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Completed);
    }

    [Fact]
    public async Task An_Unexpired_Record_Survives_Another_Tenants_Flood()
    {
        // The global ceiling is a shared resource. Without a per-tenant bound,
        // one tenant minting keys in a loop revokes every other tenant's
        // records, and their retries then re-run completed operations.
        var (store, clock) = NewStore();
        var victim = await store.TryClaimAsync(OtherTenant, Key, Fingerprint, default);
        await store.CompleteAsync(OtherTenant, Key, victim.Token, Response(200), default);

        var refused = 0;
        for (var i = 0; i < InMemoryIdempotencyStore.MaxEntriesPerTenant * 2; i++)
        {
            clock.Advance(InMemoryIdempotencyStore.SweepInterval);
            var claim = await store.TryClaimAsync(Tenant, $"flood-{i:D6}", Fingerprint, default);
            if (claim.Outcome == IdempotencyClaim.CapacityExhausted)
            {
                refused++;
                continue;
            }

            await store.CompleteAsync(Tenant, $"flood-{i:D6}", claim.Token, Response(200), default);
        }

        refused.Should().BeGreaterThan(0, "the flooding tenant hits its own allowance");
        (await store.TryClaimAsync(OtherTenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Completed,
                "the victim's record had not expired, so nothing may drop it");
    }

    [Fact]
    public async Task Capacity_Comes_Back_When_Records_Expire()
    {
        // Refusing has to be temporary, or a tenant that once filled its
        // allowance would be locked out for good.
        var (store, clock) = await FilledToAllowanceAsync();

        clock.Advance(InMemoryIdempotencyStore.Retention);

        (await store.TryClaimAsync(Tenant, "after-the-window", Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Acquired);
    }

    // ---- tombstones --------------------------------------------------------

    [Fact]
    public async Task An_Outcome_Recorded_Without_A_Response_Refuses_The_Retry()
    {
        // The operation happened and its answer was not retained. Releasing the
        // key would let a retry do the work again; that is the one outcome this
        // type exists to prevent, so the retry is refused instead.
        var (store, _) = NewStore();
        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        await store.CompleteAsync(Tenant, Key, claim.Token, response: null, default);

        var retry = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        retry.Outcome.Should().Be(IdempotencyClaim.Unreplayable);
        retry.Stored.Should().BeNull();
    }

    [Fact]
    public async Task A_Tombstone_Expires_With_The_Retention_Window()
    {
        var (store, clock) = NewStore();
        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);
        await store.CompleteAsync(Tenant, Key, claim.Token, response: null, default);

        clock.Advance(InMemoryIdempotencyStore.Retention);

        (await store.TryClaimAsync(Tenant, Key, Fingerprint, default))
            .Outcome.Should().Be(IdempotencyClaim.Acquired);
    }

    // ---- fencing is observable ---------------------------------------------

    [Fact]
    public async Task A_Caller_That_Lost_Its_Claim_Is_Told_So()
    {
        // Silence here is how a side effect nobody will replay becomes
        // invisible: the operation ran, the lease had expired, and the caller
        // needs to know its outcome was not recorded.
        var (store, clock) = NewStore();
        var first = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        clock.Advance(InMemoryIdempotencyStore.ClaimTimeout);
        await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        (await store.CompleteAsync(Tenant, Key, first.Token, Response(200), default))
            .Should().BeFalse();
        (await store.AbandonAsync(Tenant, Key, first.Token, default)).Should().BeFalse();
    }

    [Fact]
    public async Task A_Caller_That_Still_Owns_Its_Claim_Records_Successfully()
    {
        var (store, _) = NewStore();
        var claim = await store.TryClaimAsync(Tenant, Key, Fingerprint, default);

        (await store.CompleteAsync(Tenant, Key, claim.Token, Response(200), default))
            .Should().BeTrue();
    }

    /// <summary>A tenant holding exactly its allowance in completed records.</summary>
    private static async Task<(InMemoryIdempotencyStore Store, FixedClock Clock)> FilledToAllowanceAsync()
    {
        var (store, clock) = NewStore();

        for (var i = 0; i < InMemoryIdempotencyStore.MaxEntriesPerTenant; i++)
        {
            var key = $"filler-{i:D5}";
            var claim = await store.TryClaimAsync(Tenant, key, Fingerprint, default);
            claim.Outcome.Should().Be(IdempotencyClaim.Acquired);
            await store.CompleteAsync(Tenant, key, claim.Token, Response(200), default);
        }

        // The census is refreshed by the sweep, so admission only sees the
        // filled map after one interval has passed.
        clock.Advance(InMemoryIdempotencyStore.SweepInterval);
        await store.TryClaimAsync(Tenant, "filler-00000", Fingerprint, default);

        return (store, clock);
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
        // The window is the sweep's own enumeration, so the test widens it
        // deliberately: each round starts with several hundred EXPIRED entries
        // for the sweep to walk while the workers race to take them over. Every
        // key may be acquired exactly once per round — the takeover — and a
        // second acquire is the bug. Between rounds the clock jumps a full claim
        // timeout, which expires everything again and re-arms the sweep.
        const int keys = 400;
        const int threads = 12;
        const int rounds = 8;

        var clock = new AtomicClock(Origin);
        var store = new InMemoryIdempotencyStore(clock);
        var names = Enumerable.Range(0, keys).Select(i => $"raced-{i:D4}").ToArray();

        foreach (var name in names)
        {
            await store.TryClaimAsync(Tenant, name, Fingerprint, default);
        }

        for (var round = 0; round < rounds; round++)
        {
            // Everything in the map is now expired, so every key is takeable
            // and the sweep has the whole map to walk.
            clock.Advance(InMemoryIdempotencyStore.ClaimTimeout);

            var acquisitions = new int[keys];
            using var start = new Barrier(threads);

            // Dedicated threads, not pool tasks: the claims complete
            // synchronously, so pool tasks would largely run one after another
            // and the interleaving this test looks for would never happen.
            var workers = Enumerable.Range(0, threads).Select(worker =>
                Task.Factory.StartNew(
                    () =>
                    {
                        start.SignalAndWait();

                        // Each worker walks the keys from a different offset, so
                        // the takeovers spread across the sweep's enumeration
                        // instead of all landing at its head.
                        for (var step = 0; step < keys; step++)
                        {
                            var i = (step + (worker * (keys / threads))) % keys;
                            var claim = store
                                .TryClaimAsync(Tenant, names[i], Fingerprint, default)
                                .GetAwaiter().GetResult();

                            if (claim.Outcome == IdempotencyClaim.Acquired)
                            {
                                Interlocked.Increment(ref acquisitions[i]);
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default)).ToArray();

            await Task.WhenAll(workers);

            acquisitions.Should().OnlyContain(count => count == 1,
                $"round {round}: each key was acquired by exactly one caller; a second "
                + "acquire means a live claim was destroyed and the operation would "
                + "have run twice");
        }
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
