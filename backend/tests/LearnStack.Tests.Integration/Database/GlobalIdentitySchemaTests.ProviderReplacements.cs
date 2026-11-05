using System.Text.RegularExpressions;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

public sealed partial class GlobalIdentitySchemaTests
{
    [Fact]
    public void Compiled_provider_census_retains_the_latest_exact_envelope_body()
    {
        var declarations = CompiledEnvelopeDeclarations();
        declarations.Should().HaveCountGreaterThan(1, "the real Platform chain contains a later envelope replacement");
        declarations.Last().Groups["replace"].Success.Should().BeTrue();
        var previous = declarations.First().Groups["body"].Value;
        var latest = declarations.Last().Groups["body"].Value;
        latest.Should().NotBe(previous);
        CompiledFunctions().Single(function => function.Name == "fn_invitation_email_envelope_guard").Body
            .Should().Be(latest, "the provider's later compiled migration must replace its initial body without normalization");
    }

    [Theory]
    [InlineData("superseded-body")]
    [InlineData("literal-whitespace")]
    public async Task Applied_provider_inspector_rejects_a_committed_old_or_changed_literal_body(string defect)
    {
        await using var clean = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        await using var dirty = await DisposableSchemaDatabase.CreateAsync(fixture.Postgres);
        var declarations = CompiledEnvelopeDeclarations();
        var current = declarations.Last();
        var mutated = defect == "superseded-body"
            ? declarations.First().Value.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)
            : current.Value.Replace("Provider envelope mutation refused.", "Provider  envelope mutation refused.", StringComparison.Ordinal);
        mutated.Should().NotBe(current.Value, "the applied control must actually change the latest declaration");
        await using (var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(dirty.MigrationConnectionString))
        {
            (await FunctionViolationsAsync(owner)).Should().BeEmpty();
            await ExecuteAsync(owner, null, mutated);
        }
        await using (var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(dirty.MigrationConnectionString))
            (await FunctionViolationsAsync(observer)).Should().Equal(["fn_invitation_email_envelope_guard:body"],
                "the exact body comparison must reject a superseded lookup or changed message even with unchanged signatures, ACLs and callers");
        await using (var observer = (NpgsqlConnection)await PostgresFixture.OpenAsync(clean.MigrationConnectionString))
            (await FunctionViolationsAsync(observer)).Should().BeEmpty();
    }

    private static Match[] CompiledEnvelopeDeclarations()
    {
        using var platform = new PlatformDbContext(new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only").Options);
        var migrations = platform.GetService<IMigrationsAssembly>();
        // Independent regression oracle: inspect this named function across the
        // entire compiled Platform chain, without the provider-boundary selector.
        return migrations.Migrations.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .SelectMany(pair => migrations.CreateMigration(pair.Value, platform.Database.ProviderName!).UpOperations.OfType<SqlOperation>())
            .SelectMany(operation => FunctionDeclaration().Matches(operation.Sql))
            .Where(declaration => declaration.Groups["name"].Value == "fn_invitation_email_envelope_guard")
            .ToArray();
    }
}
