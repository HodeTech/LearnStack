namespace LearnStack.SharedKernel.Hosting;

/// <summary>
/// Reads a <see cref="DeploymentMode"/> from configuration text the one way every host that has to agree on it does:
/// the API at start-up and the offline operator tools.
/// </summary>
/// <remarks>
/// Only a declared name is accepted, compared without regard to case and with surrounding white space ignored.
/// <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> alone also accepts an ordinal and a comma-separated
/// list, so <c>Deployment__Mode=0</c> would parse as <see cref="DeploymentMode.Development"/>: the silent default the
/// explicit key exists to remove, reached through a value that looks like a typo rather than a mode.
/// </remarks>
public static class DeploymentModeParser
{
    /// <summary>Parses <paramref name="text"/>; false when it is empty or not a declared mode name.</summary>
    public static bool TryParse(string? text, out DeploymentMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var name = text.Trim();
        foreach (var declared in Enum.GetNames<DeploymentMode>())
        {
            if (string.Equals(declared, name, StringComparison.OrdinalIgnoreCase))
            {
                mode = Enum.Parse<DeploymentMode>(declared);
                return true;
            }
        }

        return false;
    }
}
