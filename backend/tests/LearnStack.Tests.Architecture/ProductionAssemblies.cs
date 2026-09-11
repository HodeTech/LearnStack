using System.Reflection;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// Every production assembly — each project under <c>backend/src</c> — for the rules whose
/// subject is the whole binary rather than one module.
/// </summary>
/// <remarks>
/// <para>
/// <b>From the project files on disk, not a list.</b> A list is a thing an author forgets to
/// grow, and a project added without its entry is a project the rule never scanned.
/// </para>
/// <para>
/// <b>Loaded loudly.</b> An assembly that does not load is a missing project reference in
/// this test project, and the right outcome is a red build naming it rather than a smaller
/// scan that still reports green.
/// </para>
/// </remarks>
internal static class ProductionAssemblies
{
    public static IEnumerable<string> Names() =>
        Directory.EnumerateFiles(RepositoryPaths.BackendSrc(), "LearnStack.*.csproj", SearchOption.AllDirectories)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => !string.IsNullOrEmpty(name))
            .Select(name => name!)
            .Order(StringComparer.Ordinal);

    public static IEnumerable<Assembly> All() =>
        Names().Select(name =>
        {
            try
            {
                return Assembly.Load(name);
            }
            catch (FileNotFoundException exception)
            {
                throw new InvalidOperationException(
                    $"Could not load {name}; reference it from LearnStack.Tests.Architecture.csproj "
                    + "so the rules that scan every production assembly scan it too.",
                    exception);
            }
        });
}
