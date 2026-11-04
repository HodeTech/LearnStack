using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using LearnStack.Infrastructure.Email;
using LearnStack.SharedKernel.Secrets;
using LearnStack.Tests.Fixtures;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;
using static LearnStack.Tests.Integration.Database.IdentityAuthorizationHttpProof;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class BoundedAdministrationHttpTests
{
    [Theory]
    [InlineData("tr-TR", false, true)]
    [InlineData("tr-TR", true, true)]
    [InlineData("en-US", true, false)]
    [InlineData("ar", true, false)]
    public async Task Actual_invitation_create_and_reissue_preserve_enabled_regional_locale_and_render_supported_mail_copy(
        string locale, bool onlyLocale, bool turkish)
    {
        await using var proof = await CreatePreparedAsync();
        var tenant = turkish ? SeedData.Yoga : SeedData.English;
        var actor = turkish ? YogaAdmin : EnglishAdmin;
        if (onlyLocale)
            await proof.Database.ExecuteAsync(tenant, """
                UPDATE tenant_locales SET is_default=false,is_enabled=false WHERE tenant_id=@tenant;
                INSERT INTO tenant_locales(tenant_id,locale,is_default,is_enabled,sort)
                VALUES(@tenant,@locale,true,true,0)
                ON CONFLICT(tenant_id,locale) DO UPDATE SET is_default=true,is_enabled=true,sort=0;
                """, new Dictionary<string, object> { ["locale"] = locale });
        using var client = proof.Client();
        var token = proof.Token(actor, tenant);
        using var choices = await SendAsync(client, token, "/api/v1/identity/invitable-roles", host: tenant.Host);
        var role = (await JsonAsync(choices)).GetProperty("items").EnumerateArray()
            .Single(row => row.GetProperty("builtInKey").GetString() == "Learner");
        using var created = await SendAsync(client, token, "/api/v1/identity/invitations", "POST",
            JsonSerializer.Serialize(new
            {
                email = "regional-recipient@example.test",
                roleId = role.GetProperty("roleId").GetString(),
                roleVersion = role.GetProperty("version").GetString()
            }), key: Key(), host: tenant.Host);
        var receipt = await JsonAsync(created, HttpStatusCode.Created);
        var location = created.Headers.Location!.OriginalString;
        var invitationId = Guid.Parse(location["/api/v1/identity/invitations/".Length..]);
        await AssertRegionalInvitationMailAsync(proof, tenant, invitationId, locale, onlyLocale, turkish);
        using var reissued = await SendAsync(client, token, location + "/reissues", "POST", "{}",
            receipt.GetProperty("version").GetString(), Key(), host: tenant.Host);
        await JsonAsync(reissued);
        await AssertRegionalInvitationMailAsync(proof, tenant, invitationId, locale, onlyLocale, turkish);
    }

    private static async Task AssertRegionalInvitationMailAsync(IdentityAuthorizationHttpProof proof, SeedTenant tenant,
        Guid invitationId, string locale, bool onlyLocale, bool turkish)
    {
        // Observe the actual persisted generation through the ordinary app role.
        await using (var app = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await SchemaQueries.AssertApplicationRoleAsync(app);
            await IdentityMembershipFixture.ScopeAsync(app, transaction, tenant.TenantId.Value, tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId.Value : null);
            await using var command = new NpgsqlCommand("""
                SELECT i.entry_locale,(SELECT count(*) FROM tenant_locales l WHERE l.tenant_id=i.tenant_id AND l.is_enabled),
                    (SELECT locale FROM tenant_locales l WHERE l.tenant_id=i.tenant_id AND l.is_enabled AND l.is_default)
                FROM invitations i WHERE i.id=@id
                """, app, transaction);
            command.Parameters.AddWithValue("id", invitationId);
            await using var row = await command.ExecuteReaderAsync();
            Assert.True(await row.ReadAsync());
            Assert.Equal(locale, row.GetString(0));
            Assert.Equal(onlyLocale ? 1L : 2L, row.GetInt64(1));
            Assert.Equal(locale, row.GetString(2));
        }
        // Owner-only fixture observation decrypts production-staged custody; the app gains no SELECT grant.
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(proof.Database.MigrationConnectionString);
        await using var scope = await owner.BeginTransactionAsync();
        await IdentityMembershipFixture.ScopeAsync(owner, scope, tenant.TenantId.Value, tenant.MapHostToDefaultOrganization ? tenant.DefaultOrganization.OrganizationId.Value : null);
        await using var read = new NpgsqlCommand("""
            SELECT e.id,e.tenant_id,e.invitation_id,e.generation_id,e.job_id,e.key_version,e.nonce,e.ciphertext,e.tag,e.organization_id
            FROM invitation_email_envelopes e JOIN invitations i ON i.id=e.invitation_id AND i.generation_id=e.generation_id
            WHERE i.id=@id AND e.tenant_id=@tenant
            """, owner, scope);
        read.Parameters.AddWithValue("id", invitationId);
        read.Parameters.AddWithValue("tenant", tenant.TenantId.Value);
        await using var envelope = await read.ExecuteReaderAsync();
        Assert.True(await envelope.ReadAsync());
        var binding = new InvitationEmailBinding(envelope.GetGuid(0), envelope.GetGuid(1), envelope.IsDBNull(9) ? null : envelope.GetGuid(9),
            envelope.GetGuid(2), envelope.GetGuid(3), envelope.GetGuid(4));
        var encrypted = new InvitationEmailCiphertext(envelope.GetString(5), envelope.GetFieldValue<byte[]>(6),
            envelope.GetFieldValue<byte[]>(7), envelope.GetFieldValue<byte[]>(8));
        try
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(InvitationEmailFixtureConfiguration.Values).Build();
            var message = new InvitationEmailCipher(new ConfigurationSecretProvider(configuration)).Decrypt(binding, encrypted);
            Assert.Equal(turkish ? "LearnStack daveti" : "LearnStack invitation", message.Subject);
            var lead = turkish ? "Daveti inceleyin:" : "Review your invitation:";
            Assert.True(message.TextBody.Contains(lead, StringComparison.Ordinal), "Staged text uses the selected supported copy.");
            Assert.True(message.HtmlBody.Contains(lead, StringComparison.Ordinal), "Staged HTML uses the selected supported copy.");
            Assert.True(message.TextBody.Contains("/invitations#", StringComparison.Ordinal), "Staged mail retains the clean native fragment link.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encrypted.Nonce);
            CryptographicOperations.ZeroMemory(encrypted.Ciphertext);
            CryptographicOperations.ZeroMemory(encrypted.Tag);
        }
        Assert.False(await envelope.ReadAsync());
    }
}
