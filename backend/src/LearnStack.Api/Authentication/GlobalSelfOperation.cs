using LearnStack.Modules.Identity.Application.Contracts.Entry;

namespace LearnStack.Api.Authentication;

/// <summary>The complete endpoint-dependent exception; unrelated routes never acquire global-self authority.</summary>
internal sealed record GlobalSelfOperation(IdentitySelfOperation Kind, string Path, string Method)
{
    internal static IReadOnlyList<GlobalSelfOperation> All { get; } = Array.AsReadOnly<GlobalSelfOperation>(
    [
        new(IdentitySelfOperation.State, "/api/v1/identity/self", HttpMethods.Get),
        new(IdentitySelfOperation.Bootstrap, "/api/v1/identity/self/bootstrap", HttpMethods.Post),
        new(IdentitySelfOperation.Contexts, "/api/v1/identity/self/contexts", HttpMethods.Get),
    ]);

    internal static GlobalSelfOperation? ForPath(PathString path) =>
        All.SingleOrDefault(operation => string.Equals(path.Value, operation.Path, StringComparison.Ordinal));
}
