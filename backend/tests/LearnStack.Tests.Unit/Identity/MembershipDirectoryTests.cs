using System.Text;
using System.Text.Json;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Audit;
using LearnStack.Modules.Identity.Application.Contracts.Memberships;
using LearnStack.Modules.Identity.Application.Memberships;
using LearnStack.Modules.Identity.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Tenancy;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Identity;

public sealed class MembershipDirectoryTests
{
    private static readonly UserId Actor = UserId.From(Guid.CreateVersion7());
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());
    private static readonly OrganizationId Branch = OrganizationId.From(Guid.CreateVersion7());
    private static readonly MembershipId Anchor = MembershipId.From(Guid.Parse("01930000-abcd-7000-8000-00000000ffff"));
    private static readonly string Scope = MembershipDirectoryCursorCodec.Scope(Actor, Tenant, Branch);

    [Fact]
    public void Cursor_roundtrips_canonically_and_is_bound_to_actor_tenant_organization_and_operation()
    {
        var raw = MembershipDirectoryCursorCodec.Encode(Scope, Anchor);
        Assert.True(MembershipDirectoryCursorCodec.TryDecode(raw, Scope, out var decoded));
        Assert.Equal(Anchor, decoded);
        Assert.False(MembershipDirectoryCursorCodec.TryDecode(raw,
            MembershipDirectoryCursorCodec.Scope(UserId.From(Guid.CreateVersion7()), Tenant, Branch), out _));
        Assert.False(MembershipDirectoryCursorCodec.TryDecode(raw,
            MembershipDirectoryCursorCodec.Scope(Actor, TenantId.From(Guid.CreateVersion7()), Branch), out _));
        Assert.False(MembershipDirectoryCursorCodec.TryDecode(raw,
            MembershipDirectoryCursorCodec.Scope(Actor, Tenant, null), out _));
        var payload = Encoding.UTF8.GetString(Convert.FromBase64String(raw.Replace('-', '+').Replace('_', '/') + new string('=', (4 - raw.Length % 4) % 4)));
        Assert.Equal(["v", "scope", "id"], JsonDocument.Parse(payload).RootElement.EnumerateObject().Select(property => property.Name));
        Assert.DoesNotContain(Actor.Value.ToString("D"), payload, StringComparison.Ordinal);
        Assert.DoesNotContain(Tenant.Value.ToString("D"), payload, StringComparison.Ordinal);
        Assert.Equal("identity.membership.list", IdentityOperationPolicySource.MembershipDirectoryOperation);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("padding")]
    [InlineData("whitespace")]
    [InlineData("duplicate")]
    [InlineData("extra")]
    [InlineData("reordered")]
    [InlineData("unknown_version")]
    [InlineData("alternate_version")]
    [InlineData("uppercase_id")]
    [InlineData("non_v7")]
    [InlineData("encoded_oversize")]
    [InlineData("decoded_oversize")]
    [InlineData("invalid_utf8")]
    [InlineData("invalid_json")]
    public void Invalid_or_noncanonical_cursor_is_rejected_with_no_anchor(string variant)
    {
        var payload = $"{{\"v\":1,\"scope\":\"{Scope}\",\"id\":\"{Anchor.Value:D}\"}}";
        var raw = variant switch
        {
            "empty" => "",
            "padding" => Encode(payload) + "=",
            "whitespace" => Encode(" " + payload),
            "duplicate" => Encode(payload.Replace("\"v\":1,", "\"v\":1,\"v\":1,", StringComparison.Ordinal)),
            "extra" => Encode(payload.Replace("\"v\":1,", "\"v\":1,\"other\":null,", StringComparison.Ordinal)),
            "reordered" => Encode($"{{\"scope\":\"{Scope}\",\"v\":1,\"id\":\"{Anchor.Value:D}\"}}"),
            "unknown_version" => Encode(payload.Replace("\"v\":1,", "\"v\":2,", StringComparison.Ordinal)),
            "alternate_version" => Encode(payload.Replace("\"v\":1,", "\"v\":1.0,", StringComparison.Ordinal)),
            "uppercase_id" => Encode(payload.Replace(Anchor.Value.ToString("D"), Anchor.Value.ToString("D").ToUpperInvariant(), StringComparison.Ordinal)),
            "non_v7" => Encode(payload.Replace(Anchor.Value.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal)),
            "encoded_oversize" => new string('A', 1025),
            "decoded_oversize" => Encode(new string(' ', 769)),
            "invalid_utf8" => "_w",
            _ => Encode("{"),
        };
        Assert.False(MembershipDirectoryCursorCodec.TryDecode(raw, Scope, out var after));
        Assert.Null(after);
        var validation = new ListMembershipsQueryValidator().Validate(new ListMembershipsQuery(raw));
        Assert.False(validation.IsValid);
        Assert.Contains(validation.Errors, error => error.PropertyName == "cursor");
    }

    [Theory]
    [InlineData(20, 21)]
    [InlineData(1000, 101)]
    public async Task Handler_applies_shared_default_and_clamp_and_emits_forward_only_continuation(int limit, int take)
    {
        var store = Substitute.For<IMembershipDirectoryReader>();
        var rows = Enumerable.Range(0, take).Select(_ => new MembershipDirectoryItem(Guid.CreateVersion7(), Actor, Branch,
            DirectoryMembershipStatus.Active, "Context name", "en")).OrderBy(row => row.MembershipId).ToArray();
        store.ReadAsync(null, take, Arg.Any<CancellationToken>()).Returns(rows);
        var result = await new ListMembershipsQueryHandler(store, Context()).Handle(new ListMembershipsQuery(Limit: limit), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(take - 1, result.Value.Items.Count);
        Assert.True(result.Value.PageInfo.HasNext);
        Assert.False(result.Value.PageInfo.HasPrevious);
        Assert.Null(result.Value.PageInfo.PreviousCursor);
        Assert.True(MembershipDirectoryCursorCodec.TryDecode(result.Value.PageInfo.NextCursor!, Scope, out var after));
        Assert.Equal(result.Value.Items[^1].MembershipId, after!.Value.Value);
        Assert.Equal(20, new ListMembershipsQuery().Limit);
    }

    [Fact]
    public async Task Invalid_limit_or_foreign_cursor_returns_the_sent_parameter_without_reading()
    {
        var store = Substitute.For<IMembershipDirectoryReader>();
        var handler = new ListMembershipsQueryHandler(store, Context());
        foreach (var limit in new[] { 0, -1 })
        {
            var result = await handler.Handle(new ListMembershipsQuery(Limit: limit), CancellationToken.None);
            Assert.Equal("validation_failed", result.Error!.Code);
            Assert.True(result.Error.Details!.ContainsKey("limit"));
        }
        var foreign = MembershipDirectoryCursorCodec.Encode(MembershipDirectoryCursorCodec.Scope(Actor, Tenant, null), Anchor);
        var invalid = await handler.Handle(new ListMembershipsQuery(foreign), CancellationToken.None);
        Assert.Equal("validation_failed", invalid.Error!.Code);
        Assert.True(invalid.Error.Details!.ContainsKey("cursor"));
        await store.DidNotReceiveWithAnyArgs().ReadAsync(null, 0);
    }

    [Fact]
    public void Actual_directory_policy_and_MUST_audit_entry_have_the_exact_scoped_contract()
    {
        var source = new IdentityOperationPolicySource();
        var policy = Assert.Single(source.Policies, item => item.RequestType == typeof(ListMembershipsQuery));
        Assert.Equal(OperationPolicyKind.Protected, policy.Kind);
        Assert.Equal(OperationReadScope.Tenant, policy.ReadScope);
        Assert.Equal(IdentityOperationPolicySource.MembershipAdministration, policy.RequiredPermission);
        Assert.Equal(5, source.Definitions.Count());
        Assert.Equal(6, source.Policies.Count(item => item.Kind == OperationPolicyKind.FiniteTrusted
            && item.AllowedSources.SequenceEqual([AuthorizationSourceKind.Seed])));
        var catalog = new AuditCatalog([new IdentityAuditCatalogSource()]);
        Assert.True(catalog.TryGet(typeof(ListMembershipsQuery), out var registration));
        var entry = Assert.Single(registration.Entries);
        Assert.False(registration.WritesNoRow);
        Assert.Equal("identity.membership.list", entry.Operation);
        Assert.Equal(OperationClass.Must, entry.OperationClass);
        Assert.Equal(OperationType.ReadSensitive, entry.OperationType);
        Assert.Equal(typeof(Membership), entry.EntityType);
        Assert.Equal(["MembershipId", "UserId", "OrganizationId", "Status", "DisplayName", "PreferredLocale"],
            typeof(MembershipDirectoryItem).GetProperties().Select(property => property.Name));
        foreach (var status in Enum.GetValues<DirectoryMembershipStatus>())
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(new MembershipDirectoryItem(
                Anchor.Value, Actor, Branch, status, "Context name", "en")));
            Assert.Equal(status.ToString(), json.RootElement.GetProperty("Status").GetString());
        }
    }

    private static ITenantContext Context()
    {
        var context = Substitute.For<ITenantContext>();
        context.IsResolved.Returns(true);
        context.TenantId.Returns(Tenant);
        context.OrganizationId.Returns(Branch);
        context.UserId.Returns(Actor);
        return context;
    }
    private static string Encode(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
