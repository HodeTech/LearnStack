using FluentAssertions;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Results;
using LearnStack.Tools.Seeder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Canonical finite-seed adapter and real independent app-role denial durability; no human grant lookup.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
[Collection(SharedSchema.Name)]
public sealed class SeedAuthorizationActivationTests(SchemaFixture schema)
{
    [Theory]
    [InlineData("lockey_forbidden", "forbidden")]
    [InlineData("lockey_not_found", "not_found")]
    public async Task Canonical_finite_seed_refusal_uses_one_shared_qualified_evidence_lane_across_per_act_providers(
        string errorKey, string expectedCode)
    {
        await using var business = ApplicationDataSource.Build(schema.Postgres.AppConnectionString);
        await using var source = await AuthorizationEvidenceFixture.SourceAsync(schema);
        var observed = new ObservedFactory(source);
        var context = new SeedTenantContext(TenantId.From(SchemaFixture.TenantA), null);
        try
        {
            for (var act = 0; act < 2; act++)
            {
                await using var provider = SeedComposition.Build(business, context, NullLoggerFactory.Instance,
                    authorizationEvidence: observed);
                await using var scope = provider.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<IAuthorizationEvidenceSessionFactory>().Should().BeSameAs(observed);
                var behavior = scope.ServiceProvider.GetServices<IPipelineBehavior<GetTenantSeedStateQuery, Result<SeedLookup<TenantSeedDto>>>>()
                    .OfType<AuthorizationBehavior<GetTenantSeedStateQuery, Result<SeedLookup<TenantSeedDto>>>>().Single();
                var result = await behavior.Handle(new GetTenantSeedStateQuery(),
                    () => Task.FromResult(Result.Fail<SeedLookup<TenantSeedDto>>(new Error(new LocalizedMessage(errorKey)))),
                    CancellationToken.None);
                result.Error!.Code.Should().Be(expectedCode);
                source.IsQualified.Should().BeTrue();
                observed.Events.Should().HaveCount(act + 1);
                var occurrence = observed.Events[^1];
                occurrence.Draft.ActorUserId.Should().BeNull("finite seed attribution preserves the absent principal");
                occurrence.Draft.Metadata.Should().Contain("\"source\":\"seed\"");
                occurrence.Draft.Operation.Should().Be("identity.authorization.deny");
                await using var verifier = (NpgsqlConnection)await PostgresFixture.OpenAsync(schema.Postgres.PlatformConnectionString);
                await using var command = new NpgsqlCommand("""
                    SELECT outcome, actor_user_id, operation_class, error_key
                    FROM public.audit_log WHERE id=@id AND timestamp=@timestamp
                    """, verifier);
                command.Parameters.AddWithValue("id", occurrence.Draft.Id.Value);
                command.Parameters.AddWithValue("timestamp", occurrence.Draft.Timestamp);
                await using var reader = await command.ExecuteReaderAsync();
                (await reader.ReadAsync()).Should().BeTrue("the canonical adapter cannot return its denial before the actual evidence COMMIT acknowledges");
                reader.GetString(0).Should().Be("denied");
                reader.IsDBNull(1).Should().BeTrue();
                reader.GetString(2).Should().Be("Must");
                reader.GetString(3).Should().Be(errorKey);
                (await reader.ReadAsync()).Should().BeFalse();
            }
            source.IsQualified.Should().BeTrue("disposing either per-act provider cannot close the process-owned lane");
            observed.Events.Select(occurrence => occurrence.Draft.Id).Should().OnlyHaveUniqueItems();
        }
        finally { await AuthorizationEvidenceFixture.DeleteAsync(schema, observed.Events.Select(occurrence => occurrence.Draft.Id).ToArray()); }
    }

    private sealed class ObservedFactory(IAuthorizationEvidenceSessionFactory source) : IAuthorizationEvidenceSessionFactory
    {
        internal List<AuthorizationEvidenceOccurrence> Events { get; } = [];
        public Task<IAuthorizationEvidenceSession?> OpenAsync(AuthorizationEvidenceOccurrence occurrence,
            CancellationToken cancellationToken = default)
        {
            Events.Add(occurrence);
            return source.OpenAsync(occurrence, cancellationToken);
        }
    }
}
