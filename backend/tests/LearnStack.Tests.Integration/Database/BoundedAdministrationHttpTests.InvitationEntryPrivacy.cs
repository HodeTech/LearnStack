using System.Net;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Fact]
    public async Task Actual_invitation_entry_dirty_database_privacy_refuses_503_before_sensitive_function_query()
    {
        await using var proof = await CreatePreparedAsync();
        var canary = "smtp-proof-" + Guid.NewGuid().ToString("N");
        var email = canary + "@example.test";
        var offer = await SeedEntryOfferAsync(proof, email: email);
        SetEntryRecipient(proof, YogaAdmin, email);
        var token = EntryToken(proof, YogaAdmin);
        var database = new NpgsqlConnectionStringBuilder(proof.Database.AppConnectionString).Database!;
        using var quoter = new NpgsqlCommandBuilder();
        var target = quoter.QuoteIdentifier(database);
        // Only this owned disposable database's application sessions are affected.
        await parent.Postgres.ExecuteAsSuperuserAsync($"ALTER ROLE learnstack_app IN DATABASE {target} SET track_functions='all'; ALTER ROLE learnstack_app IN DATABASE {target} SET log_error_verbosity='default';");
        try
        {
            using (var client = EntryClient(proof, freshConnections: true))
            using (var refused = await EntrySendAsync(client, token, "inspections", offer))
                await ProblemAsync(refused, HttpStatusCode.ServiceUnavailable, "dependency_unavailable");
            Assert.Equal(0, proof.Issuer.UserInfoRequests);
            Assert.Equal(0, await EntryFunctionCallsAsync(proof));
            await parent.Postgres.ExecuteAsSuperuserAsync($"ALTER ROLE learnstack_app IN DATABASE {target} SET log_error_verbosity='terse';");
            using (var client = EntryClient(proof, freshConnections: true))
            using (var admitted = await EntrySendAsync(client, token, "inspections", offer))
                await JsonAsync(admitted);
            // Disposed nonpooled sessions publish their actual PostgreSQL counters.
            Assert.True(await EntryFunctionCallsAsync(proof) > 0, "the same observed real function must be counted in the clean positive control");
            Assert.False(await parent.Postgres.ServerLogContainsSyntheticCanaryAsync(canary));
        }
        finally { await parent.Postgres.ExecuteAsSuperuserAsync($"ALTER ROLE learnstack_app IN DATABASE {target} RESET ALL;"); }
    }
    private static async Task<long> EntryFunctionCallsAsync(IdentityAuthorizationHttpProof proof)
    {
        await using var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString);
        return await ScalarAsync<long>(observer, null,
            "SELECT coalesce(sum(calls),0)::bigint FROM pg_stat_user_functions WHERE schemaname='public' AND funcname='fn_identity_invitation_entry'");
    }
}
