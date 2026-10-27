using System.Net;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identity;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.TenancyIdentity;

public sealed partial class TenantRealmApiTests
{
    [Fact]
    public async Task A_known_real_provider_subject_without_actor_evidence_uses_the_stored_application_actor()
    {
        var identity = SeedData.IdentityUsers[0];
        var completeTokens = await TokensAsync(identity);
        var complete = await KeycloakTokenVerifier.ValidateAsync(_provider, completeTokens.AccessToken, "learnstack-api");
        using var client = Client(identity.Tenant, out var observation);
        using (var positive = await Send(client, identity.Tenant, completeTokens.AccessToken))
            await AccountAsync(positive, identity, observation);
        using var admin = await _provider.CreateAdminClientAsync();
        var user = await admin.UserAsync(identity.Username);
        var path = UserPath(user);
        var original = await admin.GetAsync(path);
        var attributes = original.GetProperty("attributes").Clone();
        var withoutActor = attributes.EnumerateObject().Where(property => property.Name != "user_id")
            .ToDictionary(property => property.Name, property => property.Value.Clone());
        var credentials = await admin.GetAsync(path + "/credentials");
        var subject = new GlobalIdentitySubject(_database.Registration.AuthorityId, _provider.Issuer,
            user.GetProperty("id").GetString()!);
        var creationAudits = await BindingCreationAuditCountAsync(subject);
        creationAudits.Should().Be(1L, "the fixed foundation already created this exact binding");
        var audits = await AuditCountsAsync();
        var reads = _database.Observation.Reads;

        try
        {
            (await admin.PutAsync(path, UserAttributeUpdate(user, withoutActor))).Should().Be(HttpStatusCode.NoContent);
            JsonElement.DeepEquals(await admin.GetAsync(path + "/credentials"), credentials).Should().BeTrue(
                "removing optional actor evidence changes no password or MFA credential");
            var tokens = await TokensAsync(identity);
            var actorless = await KeycloakTokenVerifier.ValidateAsync(_provider, tokens.AccessToken, "learnstack-api");
            actorless.TryGetProperty("user_id", out _).Should().BeFalse();
            (actorless.GetProperty("sub").GetString() == subject.Subject).Should().BeTrue();
            foreach (var claim in new[] { "tenant_id", "organization_id" })
            {
                var present = complete.TryGetProperty(claim, out var expected);
                actorless.TryGetProperty(claim, out var actual).Should().Be(present);
                if (present) JsonElement.DeepEquals(actual, expected).Should().BeTrue();
            }
            using var response = await Send(client, identity.Tenant, tokens.AccessToken);
            await AccountAsync(response, identity, observation);
            _database.Observation.Reads.Should().Be(reads + 1);
            (await BindingCreationAuditCountAsync(subject)).Should().Be(creationAudits);
            (await AuditCountsAsync()).Should().Equal(audits);
        }
        finally
        {
            (await admin.PutAsync(path, UserAttributeUpdate(user, attributes))).Should().Be(HttpStatusCode.NoContent);
            JsonElement.DeepEquals(await admin.GetAsync(path), original).Should().BeTrue(
                "the exact original profile and all unrelated account state are restored");
        }

        var restoredTokens = await TokensAsync(identity);
        var restored = await KeycloakTokenVerifier.ValidateAsync(_provider, restoredTokens.AccessToken, "learnstack-api");
        restored.GetProperty("user_id").GetGuid().Should().Be(identity.UserId.Value);
        (restored.GetProperty("sub").GetString() == subject.Subject).Should().BeTrue();
        using var restoredResponse = await Send(client, identity.Tenant, restoredTokens.AccessToken);
        await AccountAsync(restoredResponse, identity, observation);
        observation.Reads.Should().HaveCount(3);
    }

    private async Task<long> BindingCreationAuditCountAsync(GlobalIdentitySubject subject)
    {
        // Evidence-only owner read; all binding adoption and observed HTTP reads
        // still execute through the real non-bypass application role.
        await using var connection = (NpgsqlConnection)await PostgresFixture.OpenAsync(_database.MigrationConnectionString);
        await using var transaction = await connection.BeginTransactionAsync();
        await using var announce = new NpgsqlCommand("""
            SELECT set_config('app.identity_authority_id', @authority, true),
                set_config('app.identity_subject_key', @subject, true)
            """, connection, transaction);
        announce.Parameters.AddWithValue("authority", subject.AuthorityId.Value.ToString("D"));
        announce.Parameters.AddWithValue("subject", subject.SubjectKey.Hex);
        await announce.ExecuteNonQueryAsync();
        await using var count = new NpgsqlCommand("""
            SELECT count(*) FROM audit_log WHERE operation='identity.user.create'
                AND metadata->>'identity_authority_id'=@authority AND metadata->>'identity_subject_key'=@subject
            """, connection, transaction);
        count.Parameters.AddWithValue("authority", subject.AuthorityId.Value.ToString("D"));
        count.Parameters.AddWithValue("subject", subject.SubjectKey.Hex);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}
