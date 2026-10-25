using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Catalogue discovery controls; no application migrations or isolation claim.</summary>
[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class StorageRelationCensusTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _postgres;

    public StorageRelationCensusTests(PostgresFixture postgres) => _postgres = postgres;

    [Theory]
    [InlineData("CREATE TABLE census_probe (id integer)", "DROP TABLE census_probe")]
    [InlineData("CREATE TABLE census_probe (id integer) PARTITION BY RANGE (id)", "DROP TABLE census_probe")]
    [InlineData("CREATE MATERIALIZED VIEW census_probe AS SELECT 1 AS id", "DROP MATERIALIZED VIEW census_probe")]
    [InlineData("CREATE FUNCTION public.census_creator() RETURNS void LANGUAGE plpgsql AS 'BEGIN EXECUTE ''CRE'' || ''ATE TABLE public.census_probe(id int)''; END;'; SELECT public.census_creator();",
        "DROP TABLE census_probe; DROP FUNCTION public.census_creator()")]
    public async Task Catalogue_discovers_each_persistent_relation_kind(string create, string drop)
    {
        await using var owner = await PostgresFixture.OpenAsync(_postgres.MigrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        (await SchemaTableClasses.ReadAsync(owner, transaction)).Should().BeEmpty();
        await SchemaQueries.ExecuteAsync(owner, transaction, create);
        (await SchemaTableClasses.ReadAsync(owner, transaction)).Should().Equal("census_probe");
        await SchemaQueries.ExecuteAsync(owner, transaction, drop);
        (await SchemaTableClasses.ReadAsync(owner, transaction)).Should().BeEmpty();
    }
}
