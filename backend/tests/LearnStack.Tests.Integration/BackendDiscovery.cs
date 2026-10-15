using System.Reflection;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Messaging;
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

    /// <summary>
    /// Every audit identity: request types for MediatR and concrete handler types for
    /// integration delivery, including inherited contracts under ADR-0057.
    /// </summary>
    public static List<Type> ShippedAuditSubjects() =>
        [.. Types()
            .Where(type => type is { IsAbstract: false, IsInterface: false, IsGenericTypeDefinition: false })
            .SelectMany(type => type.GetInterfaces()
                .Where(contract => contract.IsGenericType
                    && (contract.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)
                        || contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                        || contract.GetGenericTypeDefinition() == typeof(IRequestHandler<>)))
                .Select(contract => contract.GetGenericTypeDefinition() == typeof(IIntegrationEventHandler<>)
                    ? type
                    : contract.GenericTypeArguments[0]))
            .Distinct()
            .OrderBy(type => type.FullName, StringComparer.Ordinal)];

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
            .Order(StringComparer.Ordinal)
            .Select(path => Assembly.Load(Path.GetFileNameWithoutExtension(path)))
            .SelectMany(TypesIn);

    private static Type[] TypesIn(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // A partial load is a failed census, never a smaller green population.
            throw new InvalidOperationException(
                $"Could not enumerate every type in {assembly.FullName}; the backend census is incomplete.",
                exception);
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
