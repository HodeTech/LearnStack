using System.Text.Json;
using FluentAssertions;
using LearnStack.Tests.Integration.Database;
using LearnStack.Tools.Seeder;

namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>Authenticated provider readback feeds the actual external seed admission, never API credentials.</summary>
internal static class KeycloakIdentityDatabase
{
    internal static async Task<IdentityPublicReadFixture> CreateAsync(PublicReadFixture parent, KeycloakFixture provider)
    {
        var database = await IdentityPublicReadFixture.CreateAsync(parent, provider.Issuer);
        try
        {
            using var admin = await provider.CreateAdminClientAsync();
            var subjects = new List<object>();
            foreach (var person in SeedData.IdentityUsers)
            {
                var user = await admin.UserAsync(person.Username);
                (user.GetProperty("username").GetString() == person.Username).Should().BeTrue();
                var attributes = user.GetProperty("attributes");
                (attributes.GetProperty("user_id")[0].GetString() == person.UserId.Value.ToString("D")).Should().BeTrue();
                subjects.Add(new { actorId = person.UserId.Value, opaqueSub = user.GetProperty("id").GetString() });
            }
            await using var input = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = 1,
                issuer = provider.Issuer,
                users = subjects,
            }));
            var witness = await SeedProviderWitness.ReadAsync(input, database.Registration);
            await database.SeedFoundationAsync(witness);
            return database;
        }
        catch
        {
            await database.DisposeAsync();
            throw;
        }
    }
}
