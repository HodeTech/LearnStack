using System.Text.Json;
using FluentAssertions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.Contracts.Definitions;
using Npgsql;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed partial class CustomizationProjectionTests
{
    [Theory]
    [InlineData(false, 63)]
    [InlineData(false, 64)]
    [InlineData(true, 61)]
    [InlineData(true, 64)]
    public async Task Accepted_source_depth_survives_snapshot_envelopes_for_unrelated_cold_and_partial_reads(
        bool taxonomy, int sourceDepth)
    {
        await using var database = await DisposableSchemaDatabase.CreateAsync(schema.Postgres);
        await using var source = NpgsqlDataSource.Create(database.AppConnectionString);
        var context = await ProvisionAsync(source);
        await CreateRevisionAsync(source, context, 1);
        var id = Guid.CreateVersion7();
        if (taxonomy)
        {
            (await SendAsync(source, context, new RegisterTenantLevelTaxonomyCommand(id, "deep", 1, Label,
                [new("only", Label, 0, NestedArrays(65))]))).IsFailure.Should().BeTrue("the source limit remains 64");
            (await SendAsync(source, context, new RegisterTenantLevelTaxonomyCommand(id, "deep", 1, Label,
                [new("only", Label, 0, NestedArrays(sourceDepth))]))).IsSuccess.Should().BeTrue();
            (await SendAsync(source, context, new PublishTenantLevelTaxonomyCommand(id))).IsSuccess.Should().BeTrue();
        }
        else
        {
            (await SendAsync(source, context, new RegisterTenantContentTypeCommand(id, "deep", 1, Label,
                DeepDefaultSchema(65), "default-card"))).IsFailure.Should().BeTrue("the source limit remains 64");
            var registered = await SendAsync(source, context, new RegisterTenantContentTypeCommand(id, "deep", 1, Label,
                DeepDefaultSchema(sourceDepth), "default-card"));
            registered.IsSuccess.Should().BeTrue("{0}", JsonSerializer.Serialize(registered.Error));
            (await SendAsync(source, context, new PublishTenantContentTypeCommand(id))).IsSuccess.Should().BeTrue();
        }

        await using var cache = new CacheProbe();
        await using var read = await ReadSession.OpenAsync(source, context, cache: cache);
        await AssertAppRoleAsync(read.Unit);
        // Loading either family parses both whole families, even for unrelated pins.
        var unrelated = await read.Reader.ReadAsync(Request([new("profile", 1)], [new("levels", 1)]));
        unrelated.IsSuccess.Should().BeTrue();
        unrelated.Value!.ContentTypes.Should().ContainSingle();
        unrelated.Value.Taxonomies.Should().ContainSingle();
        unrelated.Value.MissingContentTypes.Should().BeEmpty();
        unrelated.Value.MissingTaxonomies.Should().BeEmpty();
        read.Observer.Selects.Should().Be(2);
        var generation = unrelated.Value.Generation!.Value;
        var request = Request(
            taxonomy ? [new("profile", 1)] : [new("profile", 1), new("deep", 1)],
            taxonomy ? [new("levels", 1), new("deep", 1)] : [new("levels", 1)]);

        // Warm and either direction of a partial hit retain the accepted boundary pin.
        foreach (var missingFamily in new[] { "", "content-types", "taxonomies" })
        {
            if (missingFamily.Length > 0)
            {
                await cache.RemoveAsync(FamilyKey(context.TenantId, missingFamily, generation));
            }
            read.Observer.Reset();
            var result = await read.Reader.ReadAsync(request);
            result.IsSuccess.Should().BeTrue();
            result.Value!.Generation.Should().Be(generation);
            result.Value.ContentTypes.Keys.Should().BeEquivalentTo(request.ContentTypes);
            result.Value.Taxonomies.Keys.Should().BeEquivalentTo(request.Taxonomies);
            result.Value.MissingContentTypes.Should().BeEmpty();
            result.Value.MissingTaxonomies.Should().BeEmpty();
            if (taxonomy)
            {
                var definition = result.Value.Taxonomies[new("deep", 1)];
                definition.Id.Should().Be(id);
                definition.Status.Should().Be(DefinitionStatus.Active);
                var metadata = definition.Bands.Should().ContainSingle().Which.Metadata;
                using var document = JsonDocument.Parse(metadata!);
                var value = document.RootElement;
                for (var level = 0; level < sourceDepth; level++)
                {
                    value.GetArrayLength().Should().Be(1);
                    value = value[0];
                }
                value.GetInt32().Should().Be(0);
            }
            else
            {
                result.Value.ContentTypes[new("deep", 1)].Id.Should().Be(id);
                result.Value.ContentTypes[new("deep", 1)].Status.Should().Be(DefinitionStatus.Active);
                result.Value.ContentTypes[new("deep", 1)].Fields.Should().BeEmpty();
            }
            read.Observer.Selects.Should().Be(missingFamily.Length == 0 ? 1 : 2);
        }
        cache.FactoryCalls.Should().Be(0);
        read.Db.ChangeTracker.Entries().Should().BeEmpty();
    }

    private static string NestedArrays(int depth) => new string('[', depth) + "0" + new string(']', depth);

    // The root object spends one raw JSON level; default is literal instance data.
    private static string DeepDefaultSchema(int sourceDepth) =>
        "{\"$schema\":\"https://json-schema.org/draft/2020-12/schema\",\"type\":\"object\","
        + "\"properties\":{\"a\":{\"type\":\"string\"}},\"default\":"
        + NestedArrays(sourceDepth - 1) + "}";
}
