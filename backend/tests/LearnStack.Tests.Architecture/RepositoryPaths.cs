namespace LearnStack.Tests.Architecture;

/// <summary>
/// Resolves repository-relative paths from the test's runtime location
/// (`backend/tests/.../bin/Debug/netX.0/`) by walking up until the
/// repo root marker is found.
/// </summary>
internal static class RepositoryPaths
{
    public static string RepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "CLAUDE.md"))
                && Directory.Exists(Path.Combine(current.FullName, "docs")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            $"Could not locate repository root from '{AppContext.BaseDirectory}'. " +
            "Expected to find CLAUDE.md + docs/ on a parent directory.");
    }

    public static string BackendSrc() => Path.Combine(RepoRoot(), "backend", "src");

    public static string FrontendApps() => Path.Combine(RepoRoot(), "frontend", "apps");
}
