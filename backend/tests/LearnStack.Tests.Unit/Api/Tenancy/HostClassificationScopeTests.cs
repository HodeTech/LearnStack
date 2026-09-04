using FluentAssertions;
using LearnStack.Api.Tenancy;
using LearnStack.Api.Versioning;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

/// <summary>
/// <c>Host_Classification_Applies_To_Tenant_Facing_Routes_Only</c> — which paths
/// host classification runs for, and the shape of the exclusions.
/// </summary>
/// <remarks>
/// Catalogued in
/// <see href="../../../../docs/standards/21-architecture-tests-catalogue.md">Standards
/// 21 § Tenant and organization resolution</see>, from
/// <see href="../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § The reconciliation matrix</see>. Driven against the predicate rather than
/// through the middleware: the rule is about paths, and routing a request through
/// the middleware to observe it would need a resolver and a database that the
/// decision never touches.
/// </remarks>
public sealed class HostClassificationScopeTests
{
    [Theory]
    [InlineData("/api/v1/courses")]
    [InlineData("/api/v1")]
    [InlineData("/API/V1/courses")]
    public void Classification_Applies_To_The_Tenant_Facing_Surface(string path)
    {
        HostClassificationMiddleware.ClassifiesPath(new PathString(path))
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/admin/hangfire/recurring")]
    [InlineData("/api/internal/tenants")]
    [InlineData("/")]
    [InlineData("/api/v2/courses")]
    public void Classification_Does_Not_Apply_Anywhere_Else(string path)
    {
        HostClassificationMiddleware.ClassifiesPath(new PathString(path))
            .Should().BeFalse();
    }

    [Fact]
    public void Classification_Follows_Every_Live_Api_Major()
    {
        // The prefix was the literal "/api/v1". A second live major would have skipped
        // host classification entirely on its whole surface: an unknown host reaching a
        // handler instead of the bodyless 404, and a tenant-facing route running with no
        // HostClassification feature at all. ApiVersioningExtensions.LiveMajors is the one
        // place a major is declared live, so the prefixes follow it rather than restating
        // it — and this asserts the correspondence, not the current contents.
        HostClassificationMiddleware.ClassifiedPrefixes.Should().BeEquivalentTo(
            ApiVersioningExtensions.LiveMajors.Select(major => $"/api/v{major}"),
            "one classified prefix per live major, and no other source of truth");

        foreach (var major in ApiVersioningExtensions.LiveMajors)
        {
            HostClassificationMiddleware
                .ClassifiesPath(new PathString($"/api/v{major}/courses"))
                .Should().BeTrue($"v{major} is live, so its surface is classified");
        }
    }

    [Fact]
    public void The_Exclusion_List_Is_Pinned()
    {
        // The shape assertions below iterate the list, so an emptied list passes
        // them vacuously — and an emptied list means the Hub contract surface,
        // /healthz and /openapi all start being classified, each of which is a 404
        // for a caller that has no host to resolve.
        HostClassificationMiddleware.UnclassifiedPrefixes.Should().Equal(
            "/healthz", "/readyz", "/openapi", "/admin/hangfire", "/api/internal");
    }

    [Fact]
    public void The_Exclusions_Are_Prefixes_And_Not_Endpoint_Literals()
    {
        // The distinction the catalogue calls out by name. A closed allow-list of
        // literals would exclude `/api/internal` and then 404 the entire Hub
        // contract surface the first time it grew a route — and `/api/internal/*`
        // is a whole surface with its own resolver, whose tenant comes from the
        // envelope's path segment rather than from a host.
        foreach (var prefix in HostClassificationMiddleware.UnclassifiedPrefixes)
        {
            HostClassificationMiddleware
                .ClassifiesPath(new PathString($"{prefix}/deeper/still"))
                .Should().BeFalse(
                    $"{prefix} excludes everything beneath it, not just itself");
        }
    }

    [Theory]
    [InlineData("/api/v10/courses", "the second live major is not beneath the first")]
    [InlineData("/api/v1x", "nor is anything else that merely starts with its characters")]
    public void A_Prefix_Match_Is_On_Segments_Rather_Than_Characters(string path, string because)
    {
        // Measured: every other case in this file passes with StartsWithSegments
        // replaced by a plain character StartsWith, because none of them names a
        // path that the two strategies answer differently. `/api/v10` is that
        // path, and ADR-0024's own `/api/v{N}` plan makes it the shape that
        // actually arrives — under character matching the whole of a second major
        // would be swallowed by the first's prefix and classified as v1.
        //
        // Only the CLASSIFIED prefix can show the difference. For the exclusion
        // list it is unobservable through this predicate: character matching is
        // strictly wider there, and every path it would wrongly exclude
        // (`/healthzz`, `/api/internalise`) fails the `/api/v1` test anyway, so
        // both strategies answer false. Asserting on one of those would be another
        // vacuous case, which is what this one replaced.
        HostClassificationMiddleware.ClassifiesPath(new PathString(path))
            .Should().BeFalse(because);
    }
}

/// <summary>
/// <c>Tenancy:PlatformHosts</c> refuses an entry that is not already a normalized
/// effective host.
/// </summary>
/// <remarks>
/// The comparison at runtime is ordinal against a host
/// <c>EffectiveHost.Normalize</c> produced, so an entry in any other spelling
/// matches nothing — and a platform host that quietly becomes an unknown host is a
/// 404 on the operator's own entry point, discovered in production.
/// </remarks>
public sealed class PlatformHostOptionsTests
{
    [Theory]
    [InlineData("LocalHost", "uppercase never matches a lowercased host")]
    [InlineData("app.learnstack.dev.", "a trailing dot is stripped before comparison")]
    [InlineData("localhost:5001", "a port is stripped before comparison")]
    [InlineData("1.2.3.4", "an IP literal is refused as a host name")]
    [InlineData("türkçe.example.com", "an unpunycoded IDN never matches its A-label")]
    [InlineData(" ", "whitespace names no host")]
    [InlineData(null, "and a null entry is the one spelling the comparison alone lets through")]
    public void An_Unnormalized_Entry_Refuses_The_Boot(string? host, string because)
    {
        var options = new PlatformHostOptions { Hosts = [host!] };

        var act = options.Validate;

        act.Should().Throw<InvalidOperationException>(because)
            .WithMessage($"*{PlatformHostOptions.SectionName}*");
    }

    [Fact]
    public void A_Normalized_Entry_Is_Accepted_And_Compared_Ordinally()
    {
        var options = new PlatformHostOptions
        {
            Hosts = ["localhost", "app.learnstack.dev", EffectiveHost.Normalize("türkçe.example.com")!],
        };

        var hosts = options.Validate();

        hosts.Should().HaveCount(3);
        hosts.Should().Contain("xn--trke-2oa7j.example.com",
            "the A-label is what EffectiveHost.Normalize produces and what a request carries");
        hosts.Contains("LOCALHOST").Should().BeFalse("the set is ordinal, not case-insensitive");
    }

    [Fact]
    public void No_Configured_Hosts_Is_Legal()
    {
        // A deployment with no Studio entry host has none, and that is not a
        // misconfiguration — every host is then a tenant host or an unknown one.
        new PlatformHostOptions().Validate().Should().BeEmpty();
    }
}
