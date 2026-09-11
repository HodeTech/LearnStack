using System.Reflection;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// The audit catalogue as the composition roots build it: every
/// <see cref="IAuditCatalogSource"/> any backend assembly ships, merged once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Discovered, not listed.</b> The first coverage rules merged two hard-coded sources, so
/// a third module's source — or a source a composition root registers and a rule forgot —
/// would have left every rule reporting on a catalogue the running system does not have.
/// The assemblies are every project under <c>backend/src</c>, loaded by name, so a new
/// module is swept the moment it builds.
/// </para>
/// <para>
/// One place, because two rules read it — the catalogue ↔ matrix join and the
/// <c>[PublicSurface]</c> cross-check — and two private copies of a discovery are two
/// answers to what the catalogue is.
/// </para>
/// </remarks>
internal static class AuditCatalogDiscovery
{
    /// <summary>The merged catalogue of every shipped source.</summary>
    public static AuditCatalog Catalogue() =>
        new(Types()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IAuditCatalogSource).IsAssignableFrom(type))
            .Select(type => (IAuditCatalogSource)Activator.CreateInstance(type)!)
            .ToList());

    /// <summary>Every type every project under <c>backend/src</c> compiles.</summary>
    public static IEnumerable<Type> Types() =>
        Directory
            .EnumerateFiles(RepositoryPaths.BackendSrc(), "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.Contains(
                $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => Assembly.Load(Path.GetFileNameWithoutExtension(path)))
            .SelectMany(LoadableTypes);

    private static IEnumerable<Type> LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException partial)
        {
            return partial.Types.OfType<Type>();
        }
    }
}
