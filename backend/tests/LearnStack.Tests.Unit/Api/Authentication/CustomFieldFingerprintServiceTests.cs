using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LearnStack.Api.Idempotency;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Secrets;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Authentication;

public sealed class CustomFieldFingerprintServiceTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000011"));
    private static readonly TenantId OtherTenant = TenantId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000012"));
    private static readonly UserId Actor = UserId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000013"));
    private static readonly UserId OtherActor = UserId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000014"));
    private static readonly OrganizationId Organization = OrganizationId.From(Guid.Parse("018f4d40-0000-7000-8000-000000000015"));
    private const string Key = "custom-fields-key";
    private static readonly string OldKey = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
    private static readonly string NewKey = Convert.ToBase64String(Enumerable.Repeat((byte)9, 32).ToArray());

    [Fact]
    public void Only_the_nine_new_mutation_descriptors_use_the_keyed_format()
    {
        var keyed = Enum.GetValues<ProtectedAdministrationOperation>().Where(value => Descriptor(value).UsesCustomFieldFingerprint);
        Assert.Equal(new[] { ProtectedAdministrationOperation.RegisterCustomFieldDefinition, ProtectedAdministrationOperation.ReviseCustomFieldDraft,
            ProtectedAdministrationOperation.PublishCustomFieldRevision, ProtectedAdministrationOperation.RetireCustomFieldRevision,
            ProtectedAdministrationOperation.DiscardCustomFieldDraft, ProtectedAdministrationOperation.SetMemberCustomFields,
            ProtectedAdministrationOperation.SetOwnCustomFields, ProtectedAdministrationOperation.GrantOwnFieldConsent,
            ProtectedAdministrationOperation.WithdrawOwnFieldConsent }, keyed);
    }

    [Fact]
    public async Task Metadata_precedes_key_access_and_retained_id_reproduces_the_original_after_rotation()
    {
        var events = new List<string>();
        var metadata = new Metadata(events) { Result = new(false, null) };
        var secrets = new Secrets(events, "old", new() { ["old"] = OldKey, ["new"] = NewKey });
        var config = Configuration("old", "new");
        var descriptor = Descriptor(ProtectedAdministrationOperation.SetOwnCustomFields);
        var original = await new CustomFieldFingerprintService(secrets, config, metadata)
            .ComputeAsync(Http(), Scope(), descriptor, 4, CancellationToken.None);
        Assert.StartsWith("cfh1:old:", original, StringComparison.Ordinal);
        Assert.Equal("metadata", events[0]);
        Assert.Equal(Tenant, metadata.Tenant);
        Assert.Equal(Key, metadata.Key);
        events.Clear();
        secrets.Current = "new";
        // Metadata represents any physically retained row, including one past logical expiry.
        metadata.Result = new(true, "old");
        var afterRotation = await new CustomFieldFingerprintService(secrets, config, metadata)
            .ComputeAsync(Http(), Scope(), descriptor, 4, CancellationToken.None);
        Assert.Equal(original, afterRotation);
        Assert.Equal("metadata", events[0]);
        metadata.Result = new(false, null);
        var newRequest = await new CustomFieldFingerprintService(secrets, config, metadata)
            .ComputeAsync(Http(), Scope(), descriptor, 4, CancellationToken.None);
        Assert.StartsWith("cfh1:new:", newRequest, StringComparison.Ordinal);
        Assert.NotEqual(original, newRequest);
    }

    [Theory]
    [InlineData("missing-current")]
    [InlineData("missing-current-material")]
    [InlineData("missing-retained-material")]
    [InlineData("unknown-retained-id")]
    [InlineData("invalid-base64")]
    [InlineData("short-material")]
    [InlineData("duplicate-material")]
    [InlineData("invalid-id")]
    [InlineData("runtime-invalid-sentinel")]
    [InlineData("too-many")]
    public async Task Bad_ring_or_unavailable_retained_key_refuses_without_digest_fallback(string defect)
    {
        var entries = new Dictionary<string, string?> { ["old"] = OldKey, ["new"] = NewKey };
        string? current = "new";
        var selected = new CustomFieldIdempotencyKeyMetadata(true, "old");
        switch (defect)
        {
            case "missing-current": current = null; break;
            case "missing-current-material": entries["new"] = null; break;
            case "missing-retained-material": entries["old"] = null; break;
            case "unknown-retained-id": selected = new(true, "gone"); break;
            case "invalid-base64": entries["old"] = "!not-base64!"; break;
            case "short-material": entries["old"] = Convert.ToBase64String(new byte[31]); break;
            case "duplicate-material": entries["old"] = NewKey; break;
            case "invalid-id": entries["BAD"] = OldKey; break;
            case "runtime-invalid-sentinel": entries["invalid_id"] = string.Empty; break;
            case "too-many": entries["a"] = OldKey; entries["b"] = NewKey; entries["c"] = OldKey; break;
        }
        var metadata = new Metadata([]) { Result = selected };
        var service = new CustomFieldFingerprintService(new Secrets([], current, entries), Configuration(entries.Keys.ToArray()), metadata);
        await Assert.ThrowsAsync<CustomFieldFingerprintUnavailableException>(() =>
            service.ComputeAsync(Http(), Scope(), Descriptor(ProtectedAdministrationOperation.SetOwnCustomFields), 4, CancellationToken.None));
    }

    [Fact]
    public async Task Fingerprint_binds_actor_scope_operation_precondition_and_body()
    {
        var service = Service();
        Task<string> Compute(HttpContext http, ITenantContext scope, ProtectedAdministrationOperation operation, long? expected) =>
            service.ComputeAsync(http, scope, Descriptor(operation), expected, CancellationToken.None);
        var baseline = await Compute(Http(), Scope(), ProtectedAdministrationOperation.SetOwnCustomFields, 4);
        var changed = new[]
        {
            await Compute(Http(), Scope(tenant: OtherTenant), ProtectedAdministrationOperation.SetOwnCustomFields, 4),
            await Compute(Http(), Scope(actor: OtherActor), ProtectedAdministrationOperation.SetOwnCustomFields, 4),
            await Compute(Http(), Scope(organization: Organization), ProtectedAdministrationOperation.SetOwnCustomFields, 4),
            await Compute(Http(), Scope(), ProtectedAdministrationOperation.SetMemberCustomFields, 4),
            await Compute(Http(), Scope(), ProtectedAdministrationOperation.SetOwnCustomFields, 5),
            await Compute(Http("{\"value\":\"b\"}"), Scope(), ProtectedAdministrationOperation.SetOwnCustomFields, 4),
            await Compute(Http(method: "POST"), Scope(), ProtectedAdministrationOperation.SetOwnCustomFields, 4),
            await Compute(Http(query: "?x=2"), Scope(), ProtectedAdministrationOperation.SetOwnCustomFields, 4),
            await Compute(Http(path: "/different"), Scope(), ProtectedAdministrationOperation.SetOwnCustomFields, 4),
        };
        Assert.All(changed, digest => Assert.NotEqual(baseline, digest));
        Assert.Equal(changed.Length, changed.Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain("value", baseline, StringComparison.Ordinal);
        var unkeyedGuess = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("{\"value\":\"a\"}")));
        Assert.DoesNotContain(unkeyedGuess, baseline, StringComparison.Ordinal);
        Assert.True(CustomFieldIdempotencyFingerprint.TryReadKeyId(baseline, out var keyId));
        Assert.Equal("new", keyId);
    }

    [Fact]
    public async Task Keyed_format_matches_the_independent_length_framed_hmac()
    {
        var actual = await Service().ComputeAsync(Http(), Scope(), Descriptor(ProtectedAdministrationOperation.SetOwnCustomFields), 4, CancellationToken.None);
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, Convert.FromBase64String(NewKey));
        foreach (var value in new[] { "learnstack.idempotency.custom-fields.v1", Tenant.Value.ToString("D"), "",
            Actor.Value.ToString("D"), "SetOwnCustomFields", "\"4\"", "PUT", "/api/v1/identity/custom-fields", "?x=1", "{\"value\":\"a\"}" })
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hmac.AppendData(length); hmac.AppendData(bytes);
        }
        Assert.Equal("cfh1:new:" + Convert.ToHexStringLower(hmac.GetHashAndReset()), actual);
    }

    [Fact]
    public async Task Historical_metadata_selects_current_keyed_format_instead_of_an_unkeyed_fallback()
    {
        var service = new CustomFieldFingerprintService(new Secrets([], "new", new() { ["new"] = NewKey }),
            Configuration("new"), new Metadata([]) { Result = new(true, null) });
        var fingerprint = await service.ComputeAsync(Http(), Scope(), Descriptor(ProtectedAdministrationOperation.GrantOwnFieldConsent), null, CancellationToken.None);
        Assert.StartsWith("cfh1:new:", fingerprint, StringComparison.Ordinal);
        Assert.False(CustomFieldIdempotencyFingerprint.IsHistorical(fingerprint));
    }

    [Fact]
    public async Task Historical_descriptor_uses_unchanged_unkeyed_protected_frame_without_key_ring()
    {
        var http = Http();
        var context = Scope();
        var descriptor = Descriptor(ProtectedAdministrationOperation.SetOwnMembershipProfile);
        var historical = await IdempotencyFilter.ComputeFingerprintAsync(http, context, CancellationToken.None);
        var actual = await ProtectedIdempotencyFilter.ComputeFingerprintAsync(http, context, descriptor, 4, CancellationToken.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var text in new[] { "learnstack.idempotency.protected.v1", descriptor.Operation.ToString(), "\"4\"", historical })
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        Assert.Equal(Convert.ToHexStringLower(hash.GetHashAndReset()), actual);
        Assert.False(descriptor.UsesCustomFieldFingerprint);
    }

    private static CustomFieldFingerprintService Service() => new(new Secrets([], "new", new() { ["old"] = OldKey, ["new"] = NewKey }),
        Configuration("old", "new"), new Metadata([]) { Result = new(false, null) });
    private static ProtectedAdministrationDescriptor Descriptor(ProtectedAdministrationOperation operation) => ProtectedAdministrationDescriptor.For(operation);
    private static IConfiguration Configuration(params string[] ids) => new ConfigurationBuilder().AddInMemoryCollection(
        ids.Select(id => new KeyValuePair<string, string?>("Idempotency:CustomFields:Keys:" + id, "configured"))).Build();
    private static ITenantContext Scope(TenantId? tenant = null, UserId? actor = null, OrganizationId? organization = null)
    {
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(true); context.TenantId.Returns(tenant ?? Tenant);
        context.UserId.Returns(actor ?? Actor); context.OrganizationId.Returns(organization);
        return context;
    }
    private static DefaultHttpContext Http(string body = "{\"value\":\"a\"}", string method = "PUT", string path = "/api/v1/identity/custom-fields", string query = "?x=1")
    {
        var http = new DefaultHttpContext(); http.Request.Headers[IdempotentAttribute.HeaderName] = Key;
        http.Request.Method = method; http.Request.Path = path; http.Request.QueryString = new QueryString(query);
        http.Request.ContentType = "application/json"; http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return http;
    }
    private sealed class Metadata(List<string> events) : ICustomFieldIdempotencyMetadataReader
    {
        internal CustomFieldIdempotencyKeyMetadata Result { get; set; } = new(false, null);
        internal TenantId Tenant { get; private set; }
        internal string? Key { get; private set; }
        public Task<CustomFieldIdempotencyKeyMetadata> ReadKeyMetadataAsync(TenantId tenantId, string key, CancellationToken cancellationToken)
        { events.Add("metadata"); Tenant = tenantId; Key = key; return Task.FromResult(Result); }
    }
    private sealed class Secrets(List<string> events, string? current, Dictionary<string, string?> keys) : ISecretProvider
    {
        internal string? Current { get; set; } = current;
        public string? GetSecret(string key)
        {
            events.Add("secret");
            if (key == "Idempotency:CustomFields:CurrentKeyId") return Current;
            const string prefix = "Idempotency:CustomFields:Keys:";
            return key.StartsWith(prefix, StringComparison.Ordinal) ? keys.GetValueOrDefault(key[prefix.Length..]) : null;
        }
    }
}
