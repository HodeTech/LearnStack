using System.Text.RegularExpressions;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>One bounded ASCII grammar for all frozen job manifests; no culture-sensitive names.</summary>
internal static partial class BackgroundJobNames
{
    internal static bool Definition(string? value) => value is { Length: <= 150 } && DefinitionPattern().IsMatch(value);
    internal static bool ModuleResource(string? value) => value is { Length: <= 150 } && ResourcePattern().IsMatch(value);
    internal static bool QueueOrPool(string? value) => value is { Length: <= 50 } && QueuePattern().IsMatch(value);
    internal static bool Module(string? value) => value is not null && ModulePattern().IsMatch(value);

    [GeneratedRegex(@"\A[a-z][a-z0-9]*\.[a-z][a-z0-9_]*\.v[1-9][0-9]*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex DefinitionPattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9]*\.[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ResourcePattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9_]*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex QueuePattern();

    [GeneratedRegex(@"\A[a-z][a-z0-9]*\z", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ModulePattern();
}
