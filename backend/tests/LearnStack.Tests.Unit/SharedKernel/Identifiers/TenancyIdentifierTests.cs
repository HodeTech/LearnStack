using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.Identifiers;

/// <summary>
/// The two kernel-level tenancy identifiers Packet 6 introduces.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="VogenIdEmissionTests"/> proves the emitter pipeline works against a
/// synthetic id. These cases exist because <c>TenantId</c> and
/// <c>OrganizationId</c> are the two the isolation layers key on: the cache-key
/// segment, the RLS session variable, the query filter and the envelope all
/// stringify them, and every one of those breaks differently if the conversions
/// are not on. A synthetic id passing does not prove these two do — the mask is
/// per-declaration, and a declaration that forgot it compiles.
/// </para>
/// </remarks>
public sealed class TenancyIdentifierTests
{
    private static readonly Guid Sample = Guid.Parse("019712ac-1234-7000-8000-0000000000ab");

    [Theory]
    [InlineData(typeof(TenantId))]
    [InlineData(typeof(OrganizationId))]
    public void CarriesTheEfCoreHalfOfTheCanonicalMask(Type idType)
    {
        // THE assertion, and the only one here that constrains the declaration.
        // Vogen's DEFAULT Conversions already emits the System.Text.Json converter
        // and the TypeConverter — measured: a round-trip test over both passes with
        // the mask removed, so it agrees with the code rather than constraining it.
        // What LearnStackVogenDefaults.IdMask adds beyond the default is
        // EfCoreValueConverter (and its comparer), and without it every entity
        // configuration mapping this id has to hand-roll a converter or fail at
        // model build. Delete the mask and this test is what notices.
        idType.GetNestedTypes().Select(t => t.Name)
            .Should().Contain(["EfCoreValueConverter", "EfCoreValueComparer"]);
    }

    [Theory]
    [InlineData(typeof(TenantId))]
    [InlineData(typeof(OrganizationId))]
    public void RoundTripsThroughTheTwoWireFormatsItIsCarriedIn(Type idType)
    {
        // Not a mask assertion — see above, these come from Vogen's default — but
        // the two paths the id actually travels: STJ for the envelope and every
        // API payload, TypeConverter for route binding and IConfiguration. Worth
        // pinning because a future declaration could narrow the mask rather than
        // drop it.
        var id = idType.GetMethod("From", [typeof(Guid)])!.Invoke(null, [Sample]);

        var json = JsonSerializer.Serialize(id, idType);
        JsonSerializer.Deserialize(json, idType).Should().Be(id);

        TypeDescriptor.GetConverter(idType)
            .ConvertFromString(null, CultureInfo.InvariantCulture, Sample.ToString())
            .Should().Be(id);
    }

    [Theory]
    [InlineData(typeof(TenantId))]
    [InlineData(typeof(OrganizationId))]
    public void NeitherIdentifierConvertsImplicitlyToOrFromItsPrimitive(Type idType)
    {
        // The reason they are types rather than Guids: a handler that passed an
        // organization where a tenant belongs is the bug no isolation layer can
        // catch, because both are `uuid` at the database. `typeof(A) != typeof(B)`
        // would look like the assertion for that and is not — it holds for any two
        // distinct types and no mutation of these declarations can falsify it.
        //
        // What CAN be acquired is an implicit conversion: Vogen emits one on
        // request, and a single `op_Implicit` against Guid would make both ids
        // silently interchangeable through it, restoring the primitive obsession
        // ADR-0023 removed. That is falsifiable, so it is what is asserted.
        idType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name)
            .Should().NotContain("op_Implicit");

        TenantId.From(Sample).Value.Should().Be(OrganizationId.From(Sample).Value,
            "they wrap the same primitive — which is exactly why the wrappers must differ");
    }

    [Theory]
    [InlineData(typeof(TenantId))]
    [InlineData(typeof(OrganizationId))]
    public void AnUninitializedIdIsReportedAsSuch(Type idType)
    {
        // IStronglyTypedId.IsInitialized() is the only safe way to ask: Vogen's
        // generated Equals returns false when either side is uninitialized, so
        // `id.Equals(default)` answers false FOR a transient id and the guard it
        // protects never runs. AuditableEntity.EnsureValidAuditInput depends on
        // this being right.
        var uninitialized = (IStronglyTypedId<Guid>)Activator.CreateInstance(idType)!;

        uninitialized.IsInitialized().Should().BeFalse();
    }

    [Theory]
    [InlineData(typeof(TenantId))]
    [InlineData(typeof(OrganizationId))]
    public void TheseIdentifiersMintNothingOnTheirOwn(Type idType)
    {
        // No New() / NewId() / Create() factory, deliberately. A tenant id is
        // assigned by the registry that owns the Tenant aggregate — a handler that
        // generated one could not satisfy the self-keyed policy's WITH CHECK. An
        // organization id comes from the injected IGuidFactory so a test can pin
        // it (Standards 02 § Time).
        //
        // This asserts the absence of a convenience factory and nothing more.
        // `X.From(Guid.NewGuid())` still compiles — nothing here can prevent that,
        // and claiming otherwise would be a comment the assertion does not
        // support. What stops it is review plus the IClock/IGuidFactory rule.
        idType.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Select(m => m.Name)
            .Should().NotContain(["New", "NewId", "Create"]);
    }
}
