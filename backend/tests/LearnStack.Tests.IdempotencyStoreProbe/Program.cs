using System.Text;
using LearnStack.Infrastructure.Idempotency;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Idempotency;
using Npgsql;

namespace LearnStack.Tests.IdempotencyStoreProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("claim" or "complete" or "verify")) return 78;
        try
        {
            await using var source = ApplicationDataSource.Build(Required("APP"));
            var store = new PostgresIdempotencyStore(new Lazy<NpgsqlDataSource>(() => source));
            var tenant = TenantId.From(Guid.Parse(Required("TENANT")));
            var target = new IdempotencyReplayTarget(IdempotencyReplayTargetKind.Organization, Guid.Parse(Required("TARGET")));
            var claim = await store.TryClaimAsync(tenant, Required("KEY"), "process-fingerprint", CancellationToken.None);
            if (args[0] == "verify")
            {
                if (claim.Outcome != IdempotencyClaim.Completed || claim.Stored?.ReplayTarget != target
                    || claim.Stored.StatusCode != 201 || Encoding.UTF8.GetString(claim.Stored.Body) != "{\"version\":\"0\"}"
                    || !claim.Stored.Headers.TryGetValue("ETag", out var etag) || etag.Count != 1 || etag[0] != "\"0\"") return 81;
                Console.Out.WriteLine("IdempotencyProbe:verified");
                return 0;
            }
            if (claim.Outcome != IdempotencyClaim.Acquired) return 82;
            if (args[0] == "complete")
            {
                var response = new IdempotentResponse(201, "application/json",
                    new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["ETag"] = ["\"0\""],
                        ["Location"] = ["/api/v1/organizations/" + target.Id.ToString("D")],
                    }, Encoding.UTF8.GetBytes("{\"version\":\"0\"}"), target);
                if (!await store.CompleteAsync(tenant, Required("KEY"), claim.Token, response, CancellationToken.None)) return 83;
            }
            Console.Out.WriteLine("IdempotencyProbe:" + args[0]);
            Console.Out.Flush();
            try { await Task.Delay(Timeout.InfiniteTimeSpan); }
            finally { Console.Out.WriteLine("IdempotencyProbe:finally"); Console.Out.Flush(); }
            return 84;
        }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException))
        {
            Console.Error.WriteLine("IdempotencyProbe:failed");
            return 80;
        }
    }

    private static string Required(string key) => Environment.GetEnvironmentVariable("LEARNSTACK_IDEMPOTENCY_PROOF_" + key)
        ?? throw new InvalidOperationException("The idempotency proof environment is incomplete.");
}
