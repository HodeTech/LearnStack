namespace LearnStack.Tests.Integration.TenancyIdentity;

/// <summary>
/// Where the shipped realm exports live, for the cases that read the files and start no Keycloak.
/// </summary>
/// <remarks>
/// Kept apart from the Keycloak fixture on purpose: a case that only parses a file is not a Docker case,
/// and naming the fixture would route it to the job that has a Docker socket.
/// </remarks>
internal static class RealmFiles
{
    internal static string RealmPath(string realm) => Path.Combine(RepositoryRoot(), "infra", "keycloak", "realms", realm + ".json");

    internal static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !(File.Exists(Path.Combine(directory.FullName, "CLAUDE.md"))
            && Directory.Exists(Path.Combine(directory.FullName, "infra", "keycloak", "realms"))))
            directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("The repository root is missing.");
    }
}
