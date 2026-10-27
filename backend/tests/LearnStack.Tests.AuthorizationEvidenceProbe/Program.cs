using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Tests.AuthorizationEvidenceProof;

namespace LearnStack.Tests.AuthorizationEvidenceProbe;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("precommit" or "postcommit")) return 78;
        try
        {
            var postCommit = args[0] == "postcommit";
            await using var proof = await AuthorizationBarrierHarness.CreateAsync(
                Required("APP"), IdentityAuthorityId.From(Guid.Parse(Required("AUTHORITY"))), Required("ISSUER"),
                TenantId.From(Guid.Parse(Required("TENANT"))), OrganizationId.From(Guid.Parse(Required("ORGANIZATION"))),
                UserId.From(Guid.Parse(Required("ACTOR"))), Required("CORRELATION"),
                commitBoundary: committed => BoundaryAsync(committed, postCommit),
                businessId: TenantSettingId.From(Guid.Parse(Required("BUSINESS"))));
            try { await proof.RunAsync(); }
            finally { Console.Out.WriteLine("LearnStackEvidence:finally"); Console.Out.Flush(); }
            return 79;
        }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException))
        {
            Console.Error.WriteLine("LearnStackEvidence:probe_failed");
            return 80;
        }
    }

    private static async Task BoundaryAsync(bool committed, bool postCommit)
    {
        if (committed != postCommit) return;
        Console.Out.WriteLine("LearnStackEvidenceBoundary:" + (postCommit ? "postcommit" : "precommit"));
        Console.Out.Flush();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }

    private static string Required(string key) => Environment.GetEnvironmentVariable("LEARNSTACK_EVIDENCE_PROOF_" + key)
        ?? throw new InvalidOperationException("The proof environment is incomplete.");
}
