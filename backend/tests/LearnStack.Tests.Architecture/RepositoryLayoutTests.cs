using FluentAssertions;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// File-system rules that survive any reflection-based check.
/// These walk the working copy at test time, so no compiled assembly is needed.
/// </summary>
public sealed class RepositoryLayoutTests
{
    /// <summary>Cached `["web"]` so the `BeEquivalentTo` call below does not
    /// allocate a fresh array on every test invocation (CA1861).</summary>
    private static readonly string[] AllowedFrontendApps = ["web"];

    /// <summary>
    /// ADR-0018: domain-specific shapes live as tenant customization data, not code.
    /// A `Verticals/` source folder at any level under `backend/src` is forbidden.
    /// </summary>
    [Fact]
    public void No_Source_Folder_Named_Verticals()
    {
        var srcRoot = RepositoryPaths.BackendSrc();

        var offenders = Directory
            .EnumerateDirectories(srcRoot, "Verticals", SearchOption.AllDirectories)
            .ToArray();

        offenders.Should().BeEmpty(
            "ADR-0018 supersedes ADR-0011; tenant-specific shapes belong to the Customization module's data, " +
            "not to a `Verticals/` source folder. See docs/decisions/0018-tenant-driven-customization-model.md.");
    }

    /// <summary>
    /// ADR-0009: the tenant-facing frontend ships as a single Next.js application
    /// under `frontend/apps/web`. A peer `frontend/apps/studio` or `frontend/apps/portal`
    /// is deferred until the triggers in ADR-0009 fire — adding one without an ADR
    /// is a structural deviation.
    /// </summary>
    [Fact]
    public void Frontend_Has_Only_The_Web_App()
    {
        var appsRoot = RepositoryPaths.FrontendApps();

        Directory.Exists(appsRoot).Should().BeTrue(
            $"`{appsRoot}` must exist — Phase 01 ships the frontend monorepo with `apps/web`. " +
            "If you intentionally removed it, update this test and ADR-0009 together.");

        // Filter dotted directories (.tmp, .cache, .turbo, etc.) — they're tool
        // byproducts, not peer Next apps; ADR-0009 cares about the latter only.
        var appNames = Directory
            .EnumerateDirectories(appsRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrEmpty(name))
            .Where(name => !name!.StartsWith('.'))
            .ToArray();

        appNames.Should().BeEquivalentTo(
            AllowedFrontendApps,
            "ADR-0009 keeps the tenant-facing frontend as one Next.js app. " +
            "Add a new ADR before splitting (studio / portal extraction is mechanical, " +
            "but the decision must be recorded).");
    }
}
