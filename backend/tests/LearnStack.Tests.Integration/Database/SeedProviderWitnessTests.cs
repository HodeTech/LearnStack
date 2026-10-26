using System.Text;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.Tools.Seeder;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed class SeedProviderWitnessTests
{
    private static readonly IdentityAuthorityRegistration Registration = new(
        IdentityAuthorityId.From(Guid.Parse("01930000-0000-7000-8000-000000009001")),
        "http://localhost:8080/realms/learnstack");

    [Fact]
    public async Task Private_provider_facts_preserve_exact_opaque_subjects_but_do_not_grant_contextual_authority()
    {
        var source = Envelope();
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(source));
        var witness = await SeedProviderWitness.ReadAsync(input, Registration);
        var admission = new FixedSeedIdentityFoundationAdmission(witness);
        witness.Subjects.Should().HaveCount(4);
        foreach (var pair in witness.Subjects)
        {
            pair.Value.Subject.Should().StartWith(" Opaque-Subject-").And.EndWith(" ");
            (await admission.AllowsAsync(pair.Value, pair.Key)).Should().BeTrue();
            (await admission.AllowsAsync(new GlobalIdentitySubject(Registration.AuthorityId, Registration.Issuer,
                pair.Value.Subject.Trim()), pair.Key)).Should().BeFalse();
        }
        witness.ToString().Should().Be(nameof(SeedProviderWitness));
        admission.ToString().Should().Be(nameof(FixedSeedIdentityFoundationAdmission));
        (await admission.AllowsFoundationAsync(new(SeedIdentityFoundationOperation.InitializeRole, SeedData.English.TenantId, null, UserId.SystemActor,
            SeedData.IdentityRoles[0].RoleId, null, null, null, 0, 1, "unadmitted"))).Should().BeFalse();
    }

    [Fact]
    public async Task Private_input_refuses_wrong_version_issuer_cardinality_actor_duplicate_or_unbounded_data_with_fixed_diagnostics()
    {
        var source = Envelope();
        foreach (var invalid in new[]
        {
            "", "null", "[]", "{", new string('x', 8193),
            JsonSerializer.Serialize(new { version = 1, issuer = Registration.Issuer, users = Array.Empty<object>() }),
            source.Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal),
            source.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal),
            source.Replace("localhost", "127.0.0.1", StringComparison.Ordinal),
            source.Replace(SeedData.IdentityUsers[1].UserId.Value.ToString(), SeedData.IdentityUsers[0].UserId.Value.ToString(), StringComparison.Ordinal),
            source.Replace(SeedData.IdentityUsers[0].UserId.Value.ToString(), Guid.CreateVersion7().ToString(), StringComparison.Ordinal),
            source.Replace("Opaque-Subject-1", "Opaque-Subject-0", StringComparison.Ordinal),
            source.Replace("Opaque-Subject-0", new string('x', 256), StringComparison.Ordinal),
            source.Replace("\"opaqueSub\":", "\"unknown\":", StringComparison.Ordinal),
        })
        {
            await using var input = new MemoryStream(Encoding.UTF8.GetBytes(invalid));
            var act = async () => await SeedProviderWitness.ReadAsync(input, Registration);
            (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message
                .Should().Be("Development identity witness was refused.");
        }
    }

    [Fact]
    public async Task Private_input_cancellation_is_observed_before_any_capability_is_created()
    {
        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(Envelope()));
        var act = async () => await SeedProviderWitness.ReadAsync(input, Registration, new CancellationToken(true));
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    private static string Envelope() => JsonSerializer.Serialize(new
    {
        version = 1,
        issuer = Registration.Issuer,
        users = SeedData.IdentityUsers.Select((person, index) => new { actorId = person.UserId.Value, opaqueSub = $" Opaque-Subject-{index} " }),
    });
}
