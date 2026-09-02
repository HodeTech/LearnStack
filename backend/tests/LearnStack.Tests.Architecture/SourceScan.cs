namespace LearnStack.Tests.Architecture;

/// <summary>
/// Finds a literal in source, for the rules a type-reference scan cannot express.
/// </summary>
/// <remarks>
/// NetArchTest resolves type references, so it sees a constructor's accessibility
/// and a method's return type — and never a <c>new</c> expression, a raw SQL string
/// or a property write. Several catalogued rules are about exactly those, and the
/// alternative to a scan is a rule that names something it cannot check.
/// Comments and whitespace are removed first: every file these rules cover argues in
/// prose about the very literal it is forbidden to write, and the first version of
/// the sibling scan in <c>TenancyConventionTests</c> was per-line, which a line break
/// walked straight through.
/// </remarks>
internal static class SourceScan
{
    public static string SourceRoot => RepositoryPaths.BackendSrc();

    public static string KernelRoot =>
        Path.Combine(RepositoryPaths.BackendSrc(), "LearnStack.SharedKernel");

    /// <summary>
    /// Repository-relative paths of the <c>.cs</c> files under
    /// <paramref name="root"/> whose code contains <paramref name="literal"/>.
    /// </summary>
    /// <param name="except">
    /// One path, relative to <paramref name="root"/> and written with <c>/</c>
    /// separators, that is allowed to contain it. Compared as a path rather than a
    /// bare name — two files may share a name in different folders, and excluding
    /// both because one is exempt is how a rule quietly stops covering half of what
    /// it names.
    /// </param>
    public static List<string> FilesContaining(string root, string literal, string? except)
    {
        var needle = SourceText.WithoutWhitespace(literal);
        var found = new List<string>();

        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file)
                .Replace(Path.DirectorySeparatorChar, '/');

            if (relative.Split('/') is var segments
                && (segments.Contains("obj") || segments.Contains("bin")))
            {
                continue;
            }

            if (except is not null && relative.Equals(except, StringComparison.Ordinal))
            {
                continue;
            }

            var code = SourceText.WithoutWhitespace(
                SourceText.WithoutComments(File.ReadAllText(file)));

            if (code.Contains(needle, StringComparison.Ordinal))
            {
                found.Add(relative);
            }
        }

        return found;
    }
}
