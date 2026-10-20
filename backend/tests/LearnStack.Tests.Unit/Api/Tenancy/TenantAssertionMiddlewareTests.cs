using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.Api.Versioning;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

public sealed class TenantAssertionMiddlewareTests
{
    [Fact]
    public async Task Assertions_follow_every_live_classified_major_including_bare_and_case_varied_paths()
    {
        ApiVersioningExtensions.LiveMajors.Should().NotBeEmpty();
        foreach (var major in ApiVersioningExtensions.LiveMajors)
            foreach (var path in new[] { $"/api/v{major}", $"/api/v{major}/site", $"/API/V{major}/site" })
                await AssertClassificationParity(path);
    }

    [Theory]
    [InlineData("/api/v2/site")]
    [InlineData("/api/v10/site")]
    [InlineData("/api/v01/site")]
    [InlineData("/api/v1x/site")]
    [InlineData("/api/vault")]
    [InlineData("/api/internal/tenants")]
    [InlineData("/healthz")]
    [InlineData("/openapi/v1.json")]
    public Task Assertions_share_classification_segment_and_infrastructure_boundaries(string path) => AssertClassificationParity(path);

    private static async Task AssertClassificationParity(string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Headers[TenantAssertionMiddleware.TenantHeaderName] = "malformed";
        var recorder = new ScopeRecorder();
        var calls = 0;
        var middleware = new TenantAssertionMiddleware(_ => { calls++; return Task.CompletedTask; });
        var classified = HostClassificationMiddleware.ClassifiesPath(context.Request.Path);

        await middleware.InvokeAsync(context, UnresolvedTenantContext.Instance, recorder);

        context.Response.StatusCode.Should().Be(classified ? StatusCodes.Status400BadRequest : StatusCodes.Status200OK);
        calls.Should().Be(classified ? 0 : 1, "only the classified surface may reject an assertion before dispatch");
        if (classified) recorder.Unresolved.Should().Equal(TenantAssertionDimension.Tenant);
        else recorder.Unresolved.Should().BeEmpty();
    }

    private sealed class ScopeRecorder : ITenantAssertionRecorder
    {
        internal List<TenantAssertionDimension> Unresolved { get; } = [];
        public Task RecordRejectionAsync(TenantAssertionRejection rejection) => throw new InvalidOperationException("No tenant was resolved.");
        public void RecordUnresolved(TenantAssertionDimension dimension) => Unresolved.Add(dimension);
        public void RecordNoTenantRefusal(TenantNoTenantRefusal refusal) => throw new InvalidOperationException("No claim was supplied.");
    }
}
