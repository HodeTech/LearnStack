using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using LearnStack.Api.Common;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// <c>Strongly_Typed_Ids_Publish_As_Their_Primitive</c>, per
/// <see href="../../../docs/decisions/0023-strongly-typed-id-source-generator.md">ADR-0023
/// § Implementation Notes</see>, which assigns the OpenAPI mapping to Packet 4.
/// </summary>
/// <remarks>
/// The failure this guards against is a document that describes a shape the API
/// never sends: Vogen's <c>SystemTextJson</c> converter already flattens a
/// wrapper to its primitive on the wire, so without a schema transformer the
/// contract advertises <c>{"value": "018f…"}</c> for a value that travels as a
/// bare GUID — and the generated SDK is typed against the advertisement.
/// </remarks>
public sealed class StronglyTypedIdSchemaHttpTests : IDisposable
{
    private readonly ProbeHostFixture _host = new(typeof(IdSchemaProbeController));

    [Fact]
    public async Task A_Strongly_Typed_Id_Is_Published_As_Its_Primitive()
    {
        using var client = _host.CreateClient();

        var document = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/openapi/v1.json", UriKind.Relative));

        var schemas = document.GetProperty("components").GetProperty("schemas");

        // The property is a $ref to a named schema rather than an inline one,
        // which is what a reusable identifier type should be — the generated
        // SDK resolves it to a single alias instead of repeating the shape at
        // every use.
        schemas.GetProperty(nameof(IdSchemaProbeResponse))
            .GetProperty("properties").GetProperty("owner")
            .GetProperty("$ref").GetString()
            .Should().Be($"#/components/schemas/{nameof(UserId)}");

        var id = schemas.GetProperty(nameof(UserId));
        id.GetProperty("type").GetString().Should().Be("string");
        id.GetProperty("format").GetString().Should().Be("uuid");
        id.TryGetProperty("properties", out _).Should().BeFalse(
            "the wrapper's `value` member is an implementation detail of the "
            + "struct, not a field on the wire");
    }

    [Fact]
    public async Task And_The_Wire_Agrees_With_The_Document()
    {
        // The assertion above is only worth anything if the document describes
        // what the API actually sends. A contract that is self-consistently
        // wrong is the failure mode, not a mismatch either way.
        using var client = _host.CreateClient();

        var payload = await client.GetFromJsonAsync<JsonElement>(
            new Uri("/api/v1/idschemaprobe", UriKind.Relative));

        payload.GetProperty("owner").ValueKind.Should().Be(JsonValueKind.String);
        Guid.TryParse(payload.GetProperty("owner").GetString(), out _).Should().BeTrue();
    }

    public void Dispose() => _host.Dispose();
}

public sealed record IdSchemaProbeResponse(UserId Owner);

/// <summary>Returns a strongly-typed identifier, so the document has one to describe.</summary>
public sealed class IdSchemaProbeController : ApiControllerBase, ITestOnlyController
{
    private static readonly UserId Owner =
        UserId.From(Guid.Parse("018f4d40-0000-7000-8000-0000000000a1"));

    // Not static: MVC discovers actions as instance members, and a static one
    // is simply not routed.
#pragma warning disable CA1822
    [HttpGet]
    public ActionResult<IdSchemaProbeResponse> Get() => new IdSchemaProbeResponse(Owner);
#pragma warning restore CA1822
}
