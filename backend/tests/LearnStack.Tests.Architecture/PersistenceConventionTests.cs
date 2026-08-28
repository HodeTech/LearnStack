using System.Xml.Linq;
using FluentAssertions;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The persistence rules
/// <see href="../../../docs/decisions/0039-optimistic-concurrency-token.md">ADR-0039</see>
/// and
/// <see href="../../../docs/decisions/0040-ambient-unit-of-work.md">ADR-0040</see>
/// assign to Packet 6, catalogued in
/// <see href="../../../docs/standards/21-architecture-tests-catalogue.md">Standards 21
/// § Persistence: concurrency and the unit of work</see>.
/// </summary>
/// <remarks>
/// Model inspection rather than a source scan. The mistake these rules exist to
/// catch is not a forbidden call site — it is EF metadata that looks right at the
/// call site and is wrong in the model, which is exactly what a scan cannot see.
/// </remarks>
public sealed class PersistenceConventionTests
{
    [Fact]
    public void Aggregates_With_Optimistic_Concurrency_Map_RowVersion()
    {
        // Three properties of the metadata, not one, because the two ways of
        // getting this wrong fail different ones:
        //
        //   IsRowVersion() / ValueGeneratedOnAddOrUpdate() leave both save
        //   behaviours at Ignore, and EF then omits row_version from the UPDATE
        //   entirely — the token stays 0 for the life of the row and every lost
        //   update succeeds while reporting success (ADR-0039 Amendment 1).
        //
        //   HasDefaultValue(0L) — needed for the DDL template's DEFAULT 0 — leaves
        //   ValueGenerated at OnAdd on its own. That one is benign today and is
        //   still rejected: it is a store-generated declaration on a column the
        //   aggregate increments, and the next reader of the model has to work out
        //   which of the two mistakes it is. `.ValueGeneratedNever()` states it
        //   (ADR-0039 Amendment 2).
        using var context = BuildTenancyContext();

        var offenders = new List<string>();

        foreach (var entity in context.Model.GetEntityTypes())
        {
            if (!typeof(IOptimisticConcurrency).IsAssignableFrom(entity.ClrType))
            {
                continue;
            }

            var version = entity.FindProperty(nameof(IOptimisticConcurrency.Version));

            if (version is null
                || version.GetColumnName() != "row_version"
                || !version.IsConcurrencyToken
                || version.ValueGenerated != ValueGenerated.Never
                || version.GetBeforeSaveBehavior() != PropertySaveBehavior.Save
                || version.GetAfterSaveBehavior() != PropertySaveBehavior.Save)
            {
                offenders.Add(
                    $"{entity.ClrType.Name}: column={version?.GetColumnName() ?? "<unmapped>"} "
                    + $"token={version?.IsConcurrencyToken} valueGenerated={version?.ValueGenerated} "
                    + $"before={version?.GetBeforeSaveBehavior()} after={version?.GetAfterSaveBehavior()}");
            }
        }

        offenders.Should().BeEmpty(
            "row_version is IsConcurrencyToken() + ValueGeneratedNever(), and nothing "
            + "that tells EF the database generates it (ADR-0039; Standards 05 § Concurrency)");

        // A model with no IOptimisticConcurrency entity would pass the loop above
        // without inspecting anything, which is the same defect as an inclusion
        // list that matches nothing.
        context.Model.GetEntityTypes()
            .Count(e => typeof(IOptimisticConcurrency).IsAssignableFrom(e.ClrType))
            .Should().BeGreaterThan(0, "the rule must be reading a model that has aggregates in it");
    }

    [Fact]
    public void Migration_Startup_Project_References_EntityFrameworkCore_Design()
    {
        // `dotnet ef` resolves the design package from the STARTUP project, and
        // `make migrate` names LearnStack.Api. Without the reference the tool
        // refuses before it opens a connection — "Your startup project
        // 'LearnStack.Api' doesn't reference Microsoft.EntityFrameworkCore.Design"
        // — and Packet 6 shipped a migration in exactly that state: green under
        // Testcontainers, which calls Database.MigrateAsync() directly, and
        // inapplicable by the one path Standards 05 § Database roles documents.
        var startupProject = Path.Combine(
            RepositoryPaths.BackendSrc(), "LearnStack.Api", "LearnStack.Api.csproj");

        var references = XDocument.Load(startupProject)
            .Descendants("PackageReference")
            .Select(element => element.Attribute("Include")?.Value)
            .ToList();

        references.Should().Contain("Microsoft.EntityFrameworkCore.Design",
            "`make migrate` passes --startup-project backend/src/LearnStack.Api, and "
            + "dotnet ef resolves the design-time package from there");
    }

    /// <summary>
    /// Builds the Tenancy model without a database.
    /// </summary>
    /// <remarks>
    /// A connection string is required to configure the provider and is never
    /// opened: <c>DbContext.Model</c> is built from the configurations alone. The
    /// value is deliberately not a real credential.
    /// </remarks>
    private static TenancyDbContext BuildTenancyContext() =>
        new(new DbContextOptionsBuilder<TenancyDbContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only")
            .Options);
}
