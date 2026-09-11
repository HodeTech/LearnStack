using System.Reflection;
using LearnStack.SharedKernel.Audit;
using MediatR;

namespace LearnStack.Tests.Integration;

/// <summary>
/// What the backend ships, found by reflection over every project under <c>backend/src</c>
/// rather than listed — so a new module is covered the moment it builds.
/// </summary>
internal static class BackendDiscovery
{
    /// <summary>Every closed request-handler contract a backend assembly implements.</summary>
    public static List<Type> HandlerContracts() =>
        [.. Types()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.IsGenericType
                && (contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                    || contract.GetGenericTypeDefinition() == typeof(IRequestHandler<>)))
            .Distinct()];

    /// <summary>Every request type with a handler — what the pipeline can be asked to run.</summary>
    public static List<Type> ShippedRequestTypes() =>
        [.. HandlerContracts().Select(contract => contract.GenericTypeArguments[0]).Distinct()];

    /// <summary>Every <see cref="IAuditCatalogSource"/> a backend assembly ships, constructed directly.</summary>
    public static List<IAuditCatalogSource> CatalogueSources() =>
        [.. Types()
            .Where(type => type is { IsAbstract: false, IsInterface: false }
                && typeof(IAuditCatalogSource).IsAssignableFrom(type))
            .Select(type => (IAuditCatalogSource)Activator.CreateInstance(type)!)];

    private static IEnumerable<Type> Types() =>
        Directory
            .EnumerateFiles(BackendSrc(), "*.csproj", SearchOption.AllDirectories)
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

    private static string BackendSrc()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LearnStack.slnx")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(
            directory?.FullName
                ?? throw new InvalidOperationException("LearnStack.slnx was not found above the test binaries."),
            "src");
    }
}
