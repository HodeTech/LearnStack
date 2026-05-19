namespace LearnStack.SharedKernel.Results;

public sealed record Error(
    string Code,
    string Message,
    IReadOnlyDictionary<string, string[]>? Details = null)
{
    public static readonly Error None = new("none", string.Empty);
}
