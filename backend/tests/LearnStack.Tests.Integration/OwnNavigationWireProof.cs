using System.Reflection;
using System.Text.Json;
using LearnStack.Api.Authentication;
using LearnStack.Api.Identity;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnStack.Tests.Integration;

internal static class OwnNavigationWireProof
{
    internal const int MaximumBytes = 256 * 1024;
    private static readonly string[] PageFields = ["items", "pageInfo"];
    private static readonly string[] ItemFields = ["canUseCurrentHost", "entryOrigin", "organizationId", "organizationName", "tenantId", "tenantName"];
    private static readonly string[] PageInfoFields = ["hasNext", "hasPrevious", "nextCursor", "previousCursor"];

    internal static string LongHost(Guid id)
    {
        var host = id.ToString("N") + new string('a', 13) + "." + new string('b', 63)
            + "." + new string('c', 63) + "." + new string('d', 63) + ".example.invalid";
        Assert.Equal(EffectiveHost.MaxLength, host.Length);
        Assert.Equal(host, EffectiveHost.Normalize(host));
        return host;
    }

    // Exercise the exact internal producer selection without widening its production surface.
    // The PostgreSQL test separately reaches this helper through the real request pipeline.
    internal static OwnTenantNavigationItem[] Select(IReadOnlyList<OwnTenantNavigationItem> items, int limit)
    {
        var type = typeof(OwnTenantNavigationReader).Assembly.GetType(
            "LearnStack.Modules.Tenancy.Infrastructure.Persistence.OwnTenantNavigationPageBudget", throwOnError: true)!;
        var method = type.GetMethod("Select", BindingFlags.NonPublic | BindingFlags.Static)!;
        return Assert.IsType<OwnTenantNavigationItem[]>(method.Invoke(null, [items, limit]));
    }

    internal static byte[] Serialize(IReadOnlyList<OwnTenantNavigationItem> items, PageInfo pageInfo, JsonSerializerOptions options)
    {
        var origins = new NativeEntryOriginOptions(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Identity:NativeSession:HttpsPort"] = "65535" }).Build());
        var page = new OwnMembershipContextPage(items.Select(item => new OwnMembershipContextResponse(
            item.TenantId.Value.ToString("D"), item.OrganizationId?.Value.ToString("D"), item.TenantName, item.OrganizationName,
            origins.ForHost(item.EntryHost), false)).ToArray(), pageInfo);
        return JsonSerializer.SerializeToUtf8Bytes(page, options);
    }

    // Mirror the SDK's body-byte gate and strict contexts/page shape against actual MVC bytes.
    internal static void AssertSdkReadable(byte[] bytes, int expectedCount)
    {
        Assert.InRange(bytes.Length, 1, MaximumBytes);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        Assert.Equal(PageFields, root.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        var items = root.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(expectedCount, items.Length);
        Assert.InRange(items.Length, 0, 100);
        foreach (var item in items)
        {
            Assert.Equal(ItemFields,
                item.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
            Assert.True(Guid.TryParseExact(item.GetProperty("tenantId").GetString(), "D", out _));
            var name = item.GetProperty("tenantName").GetString();
            Assert.False(string.IsNullOrWhiteSpace(name));
            Assert.InRange(name!.Length, 1, MappedLength.DisplayName);
            Assert.True(JsonValue.IsStorableText(name));
            if (item.GetProperty("organizationId").ValueKind == JsonValueKind.Null)
                Assert.Equal(JsonValueKind.Null, item.GetProperty("organizationName").ValueKind);
            else
            {
                Assert.True(Guid.TryParseExact(item.GetProperty("organizationId").GetString(), "D", out _));
                var branchName = item.GetProperty("organizationName").GetString();
                Assert.False(string.IsNullOrWhiteSpace(branchName));
                Assert.InRange(branchName!.Length, 1, MappedLength.DisplayName);
                Assert.True(JsonValue.IsStorableText(branchName));
            }
            var origin = item.GetProperty("entryOrigin").GetString();
            var uri = new Uri(origin!, UriKind.Absolute);
            Assert.Equal("https", uri.Scheme);
            Assert.Equal(origin, uri.GetLeftPart(UriPartial.Authority));
            Assert.Equal(JsonValueKind.False, item.GetProperty("canUseCurrentHost").ValueKind);
        }
        var info = root.GetProperty("pageInfo");
        Assert.Equal(PageInfoFields,
            info.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal));
        Assert.False(info.GetProperty("hasPrevious").GetBoolean());
        Assert.Equal(JsonValueKind.Null, info.GetProperty("previousCursor").ValueKind);
        if (info.GetProperty("hasNext").GetBoolean())
            Assert.InRange(info.GetProperty("nextCursor").GetString()!.Length, 1, 1024);
        else Assert.Equal(JsonValueKind.Null, info.GetProperty("nextCursor").ValueKind);
    }
}
