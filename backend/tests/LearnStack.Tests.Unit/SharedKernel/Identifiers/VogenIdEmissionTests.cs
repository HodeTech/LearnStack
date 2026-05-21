using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.Tests.Unit.SharedKernel.Domain;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Identifiers;

/// <summary>
/// End-to-end smoke test for the Vogen emitter pipeline (per ADR-0023).
/// The synthetic <see cref="TestId"/> declares the canonical annotation
/// <c>[ValueObject&lt;Guid&gt;(LearnStackVogenDefaults.IdMask)]</c>; this
/// fixture asserts the four emitted artefacts are wired:
///   - System.Text.Json round trip (Conversions.SystemTextJson).
///   - TypeConverter parse/format (Conversions.TypeConverter — the path
///     ASP.NET Core minimal-API route binding and IConfiguration use).
///   - <see cref="IStronglyTypedId{TKey}.Value"/> projection.
///   - Type-safe inequality between two arbitrary IDs.
/// EF Core converter / minimal-API model binder are exercised at the
/// integration-test layer when the first DbContext lands (Packet 6+).
/// </summary>
public sealed class VogenIdEmissionTests
{
    [Fact]
    public void IStronglyTypedId_Value_ExposesUnderlyingGuid()
    {
        var guid = Guid.CreateVersion7();
        var id = TestId.From(guid);

        ((IStronglyTypedId<Guid>)id).Value.Should().Be(guid);
    }

    [Fact]
    public void SystemTextJson_RoundTrip_PreservesValue()
    {
        var original = TestId.New();

        var json = JsonSerializer.Serialize(original);
        var decoded = JsonSerializer.Deserialize<TestId>(json);

        decoded.Should().Be(original);
    }

    [Fact]
    public void TypeConverter_RoundTrips_ViaString()
    {
        // Conversions.TypeConverter is the artefact ASP.NET Core minimal-API
        // route binding (and IConfiguration binding) rely on. Asserting the
        // converter exists and round-trips proves the mask wired the
        // TypeConverter the runtime will discover.
        var original = TestId.New();
        var converter = TypeDescriptor.GetConverter(typeof(TestId));

        converter.Should().NotBeNull();
        converter.CanConvertTo(typeof(string)).Should().BeTrue();
        converter.CanConvertFrom(typeof(string)).Should().BeTrue();

        var encoded = converter.ConvertToString(null, CultureInfo.InvariantCulture, original);
        var decoded = (TestId)converter.ConvertFromString(null!, CultureInfo.InvariantCulture, encoded!)!;

        decoded.Should().Be(original);
        ((IStronglyTypedId<Guid>)decoded).Value.Should().Be(((IStronglyTypedId<Guid>)original).Value);
    }

    [Fact]
    public void TwoIdsWithDifferentGuids_AreNotEqual()
    {
        TestId.New().Should().NotBe(TestId.New());
    }
}
