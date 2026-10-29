using LearnStack.Api.Authentication;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LearnStack.Api.Idempotency;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>Served contracts, not a hand-maintained snapshot; live application authority has separate PostgreSQL HTTP tests.</summary>
public sealed class AdministrationOpenApiTests(ProductionHostFixture fixture) : IClassFixture<ProductionHostFixture>
{
    private static readonly string[] ProfileFields = ["displayName", "preferredLocale"];

    private static readonly (string Path, string Method, string Operation, string? Input)[] Operations =
    [
        ("identity/organization-memberships", "get", "ListOrganizationMemberships", null),
        ("identity/memberships/{id}", "get", "GetMembershipDetail", null),
        ("identity/memberships/{id}/profile", "put", "SetMembershipProfile", "MembershipProfileInput"),
        ("identity/memberships/{id}/role-assignments", "put", "ReplaceMembershipRoleAssignments", "MembershipRolesInput"),
        ("identity/memberships/{id}/status", "put", "ChangeMembershipStatus", "MembershipStatusInput"),
        ("identity/me/profile", "get", "GetOwnMembershipProfile", null),
        ("identity/me/profile", "put", "SetOwnMembershipProfile", "MembershipProfileInput"),
        ("identity/me/contexts", "get", "ListOwnMembershipContexts", null),
        ("identity/me/capabilities", "get", "GetOwnCapabilities", null),
        ("identity/roles", "get", "ListRoles", null),
        ("identity/roles/{id}", "get", "GetRoleDetail", null),
        ("identity/permissions", "get", "GetPermissionRegistry", null),
        ("identity/roles/{id}/grants", "put", "ChangeRoleGrants", "RoleGrantsInput"),
        ("identity/roles/{id}/manifest-adoptions", "post", "AdoptRoleGrantManifest", "RoleManifestInput"),
        ("tenancy/organizations", "get", "ListOrganizations", null),
        ("tenancy/organizations/{id}", "get", "GetOrganizationDetail", null),
        ("tenancy/organizations", "post", "CreateManagedOrganization", "OrganizationCreateInput"),
        ("tenancy/organizations/{id}", "put", "RenameOrganization", "OrganizationRenameInput"),
        ("tenancy/organizations/{id}/archive", "post", "ArchiveOrganization", "EmptyAdministrationInput"),
        ("tenancy/default-organization", "put", "SelectDefaultOrganization", "DefaultOrganizationInput"),
    ];

    [Fact]
    public async Task All_twenty_admitted_actions_publish_exact_identity_security_body_errors_and_success_metadata()
    {
        using var client = fixture.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var paths = document.GetProperty("paths");
        Assert.Equal(20, Operations.Length);
        foreach (var declaration in Operations)
        {
            var operation = paths.GetProperty("/api/v1/" + declaration.Path).GetProperty(declaration.Method);
            Assert.Equal(declaration.Operation, operation.GetProperty("operationId").GetString());
            var security = Assert.Single(operation.GetProperty("security").EnumerateArray());
            Assert.Equal("TenantBearer", Assert.Single(security.EnumerateObject()).Name);
            var responses = operation.GetProperty("responses");
            foreach (var status in new[] { "400", "401", "403", "404", "405", "413", "429", "500", "503" })
                Assert.Equal("#/components/schemas/ProblemDetails", responses.GetProperty(status).GetProperty("content")
                    .GetProperty("application/problem+json").GetProperty("schema").GetProperty("$ref").GetString());
            var created = declaration.Operation == "CreateManagedOrganization";
            var success = responses.GetProperty(created ? "201" : "200");
            Assert.True(success.GetProperty("headers").GetProperty("Cache-Control").GetProperty("required").GetBoolean());
            if (declaration.Input is null)
            {
                Assert.False(operation.TryGetProperty("requestBody", out _));
                continue;
            }
            Assert.Equal("#/components/schemas/" + declaration.Input, operation.GetProperty("requestBody")
                .GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
            var parameters = operation.GetProperty("parameters").EnumerateArray().ToArray();
            Assert.True(Assert.Single(parameters, item => item.GetProperty("name").GetString() == "Idempotency-Key")
                .GetProperty("required").GetBoolean());
            Assert.Equal(created ? 0 : 1, parameters.Count(item => item.GetProperty("name").GetString() == "If-Match"));
            Assert.True(success.GetProperty("headers").GetProperty("ETag").GetProperty("required").GetBoolean());
            Assert.True(success.GetProperty("headers").TryGetProperty(IdempotentAttribute.ReplayedHeaderName, out _));
            Assert.Equal(created, success.GetProperty("headers").TryGetProperty("Location", out _));
            Assert.True(responses.TryGetProperty("409", out _));
        }
        Assert.True(paths.TryGetProperty("/api/v1/public/site", out _));
        Assert.True(paths.TryGetProperty("/api/v1/account/site", out _));
        Assert.True(paths.TryGetProperty("/api/v1/identity/memberships", out _));
    }

    [Fact]
    public async Task Served_schemas_keep_versions_lossless_profiles_complete_and_capabilities_separate()
    {
        using var client = fixture.CreateClient();
        var document = await client.GetFromJsonAsync<JsonElement>(new Uri("/openapi/v1.json", UriKind.Relative));
        var schemas = document.GetProperty("components").GetProperty("schemas");
        foreach (var name in new[] { "AdministrationVersionReceipt", "MembershipDetailResponse", "OwnMembershipProfileResponse",
            "RoleSummaryResponse", "RoleDetailResponse", "OrganizationResponse" })
            Assert.Equal("string", schemas.GetProperty(name).GetProperty("properties").GetProperty("version").GetProperty("type").GetString());
        Assert.Equal("string", schemas.GetProperty("OrganizationDirectoryResponse").GetProperty("properties")
            .GetProperty("tenantVersion").GetProperty("type").GetString());
        foreach (var name in Operations.Where(item => item.Input is not null).Select(item => item.Input!).Distinct(StringComparer.Ordinal))
            Assert.False(schemas.GetProperty(name).GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(ProfileFields, schemas.GetProperty("MembershipProfileInput")
            .GetProperty("required").EnumerateArray().Select(value => value.GetString()).Order(StringComparer.Ordinal));
        Assert.Equal(9, schemas.GetProperty("OwnCapabilitiesDto").GetProperty("properties").EnumerateObject().Count());
        var capabilities = document.GetProperty("paths").GetProperty("/api/v1/identity/me/capabilities").GetProperty("get")
            .GetProperty("responses").GetProperty("200").GetProperty("headers");
        Assert.False(capabilities.TryGetProperty("ETag", out _));
        var context = schemas.GetProperty("OwnMembershipContextResponse").GetProperty("properties");
        Assert.True(context.TryGetProperty("entryOrigin", out _));
        Assert.True(context.TryGetProperty("canUseCurrentHost", out _));
        Assert.False(context.TryGetProperty("entryHost", out _));
        Assert.False(context.TryGetProperty("subjectKey", out _));
        var pairs = schemas.GetProperty("AdministrationGrantResponse").GetProperty("oneOf").EnumerateArray()
            .Select(item => item.GetProperty("properties")).Select(properties =>
                Assert.Single(properties.GetProperty("scope").GetProperty("enum").EnumerateArray()).GetString() + ":" +
                Assert.Single(properties.GetProperty("key").GetProperty("enum").EnumerateArray()).GetString()).ToArray();
        Assert.Equal(8, pairs.Length);
        Assert.Equal(8, pairs.Distinct(StringComparer.Ordinal).Count());
        Assert.Contains("Tenant:identity.role.admin", pairs);
        Assert.Contains("Organization:identity.membership.admin", pairs);
        Assert.DoesNotContain("Organization:identity.role.admin", pairs);
    }

    [Fact]
    public void Actual_MVC_administration_metadata_has_one_declaration_and_its_exact_framework_projection()
    {
        var endpoints = fixture.Services.GetRequiredService<EndpointDataSource>().Endpoints;
        var classifier = fixture.Services.GetRequiredService<OperationHttpClassifier>();
        foreach (var operation in Operations)
        {
            var endpoint = Assert.Single(endpoints, candidate => candidate.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == operation.Operation);
            var action = Assert.IsType<ControllerActionDescriptor>(endpoint.Metadata.GetMetadata<ControllerActionDescriptor>());
            var keyed = endpoint.Metadata.GetOrderedMetadata<IdempotentAttribute>();
            var filters = action.FilterDescriptors.Where(filter => filter.Filter is IdempotentAttribute).ToArray();
            var declarations = action.EndpointMetadata.OfType<IdempotentAttribute>().ToArray();
            if (operation.Input is not null)
            {
                var declaration = Assert.Single(declarations);
                Assert.Same(declaration, Assert.Single(filters).Filter);
                Assert.Equal(2, keyed.Count);
                Assert.All(keyed, item => Assert.Same(declaration, item));
            }
            else
            {
                Assert.Empty(keyed);
                Assert.Empty(filters);
                Assert.Empty(declarations);
            }
            Assert.True(classifier.Classify(endpoint, out _) == HttpOperationClassification.Application,
                $"{operation.Operation}: keyed={keyed.Count}, unique={keyed.Distinct(ReferenceEqualityComparer.Instance).Count()}, filters={filters.Length}, declarations={declarations.Length}");
        }
    }

    [Theory]
    [InlineData("identity/me/profile")]
    [InlineData("identity/me/capabilities")]
    [InlineData("identity/organization-memberships")]
    [InlineData("identity/roles")]
    [InlineData("identity/permissions")]
    [InlineData("tenancy/organizations")]
    [InlineData("tenancy/default-organization")]
    public async Task Unauthenticated_administration_responses_keep_no_store(string path)
    {
        using var client = fixture.CreateClient();
        using var request = new HttpRequestMessage(path == "tenancy/default-organization" ? HttpMethod.Put : HttpMethod.Get,
            new Uri("/api/v1/" + path, UriKind.Relative));
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Null(response.Headers.ETag);
    }
}
