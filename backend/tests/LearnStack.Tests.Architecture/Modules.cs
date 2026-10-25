using System.Reflection;
using LearnStack.Modules.Audit.Domain;
using LearnStack.Modules.Audit.Infrastructure.Persistence;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Customization.Infrastructure.Persistence;
using LearnStack.Modules.Education.Domain;
using LearnStack.Modules.Education.Infrastructure.Persistence;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Identity.Infrastructure.Persistence;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.Modules.Tenancy.Infrastructure.Persistence;
using LearnStack.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The modules this repository ships — their names, and the models of the ones
/// that have a schema.
/// </summary>
internal static class Modules
{
    private static readonly Lazy<IReadOnlyList<string>> Discovered = new(() =>
        Directory
            .EnumerateDirectories(Path.Combine(RepositoryPaths.BackendSrc(), "Modules"))
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToList());

    /// <summary>Every module's short name, ordinal-sorted.</summary>
    /// <remarks>
    /// <para>
    /// <b>Discovered, not written down.</b> Three test classes needed this list
    /// and each held its own copy, which is the arrangement
    /// <c>Every_Module_With_A_Schema_Is_Swept</c> exists to prevent one level up:
    /// a module absent from a hard-coded list is invisible to the guard that
    /// would have reported it missing, so the guard is only ever as complete as
    /// the copy it happens to read. <c>backend/src/Modules</c> cannot go stale —
    /// a module that exists has a directory.
    /// </para>
    /// <para>
    /// Directories only, so <c>Directory.Build.props</c> and any stray file
    /// beside the modules contribute nothing, and <c>bin</c> / <c>obj</c> live a
    /// level deeper inside each project rather than here.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> Names => Discovered.Value;

    /// <summary>
    /// Every module that has a schema, paired with the context that maps it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Enumerated, and that is the point.</b> The rules that read this used to
    /// name one assembly and one context each, so a second module's entities were
    /// invisible to them — a sweep that has silently stopped covering what it
    /// names is worse than no sweep, because the name still reads as a guarantee.
    /// <c>Every_Module_With_A_Schema_Is_Swept</c> is what stops the list going
    /// stale: it fails when a module ships a <c>[TenantOwned]</c> entity and does
    /// not appear here.
    /// </para>
    /// <para>
    /// A module with no schema yet contributes nothing and is not listed. The
    /// guard is on the reverse direction — having a schema and not being swept.
    /// </para>
    /// </remarks>
    private static readonly (Assembly Domain, Func<DbContext> Context, bool TenantScoped)[] Models =
    [
        (typeof(Tenant).Assembly, ModelOnly<TenancyDbContext>, true),
        (typeof(TenantContentType).Assembly, ModelOnly<CustomizationDbContext>, true),
        (typeof(AuditEntry).Assembly, ModelOnly<AuditDbContext>, true),
        (typeof(Course).Assembly, ModelOnly<EducationDbContext>, true),
        (typeof(User).Assembly, ModelOnly<IdentityDbContext>, false),
    ];

    // Declared class populations, never inferred from whether a marker happened
    // to survive: deleting a marker must not delete the module from the sweep.
    public static readonly (Assembly Domain, Func<DbContext> Context)[] Mapped =
        Models.Select(model => (model.Domain, model.Context)).ToArray();
    public static readonly (Assembly Domain, Func<DbContext> Context)[] Scoped =
        Models.Where(model => model.TenantScoped).Select(model => (model.Domain, model.Context)).ToArray();

    /// <summary>
    /// A context built for its model alone. The connection string is never opened.
    /// </summary>
    /// <remarks>
    /// The model is what these rules read, and a global query filter emits no DDL
    /// and no table mapping, so the context this builds is identical whichever
    /// tenant context it holds.
    /// </remarks>
    public static DbContext ModelOnly<TContext>()
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql("Host=model-only;Database=model-only;Username=model-only")
            .Options;

        if (typeof(TContext) == typeof(IdentityDbContext))
            return new IdentityDbContext((DbContextOptions<IdentityDbContext>)(object)options,
                StaticTenantContextAccessor.Unresolved, EmptyIdentityScope.Instance);

        return (DbContext)Activator.CreateInstance(
            typeof(TContext), options, StaticTenantContextAccessor.Unresolved)!;
    }
}
