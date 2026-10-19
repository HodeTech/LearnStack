using System.Reflection;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Known composition-root assemblies; module identity is derived, never supplied as authority text.</summary>
public sealed class BackgroundJobModuleRegistration
{
    public BackgroundJobModuleRegistration(Assembly contracts, Assembly application, Assembly infrastructure)
    {
        Contracts = contracts ?? throw new ArgumentNullException(nameof(contracts));
        Application = application ?? throw new ArgumentNullException(nameof(application));
        Infrastructure = infrastructure ?? throw new ArgumentNullException(nameof(infrastructure));
        const string prefix = "LearnStack.Modules.";
        const string suffix = ".Application";
        var name = application.GetName().Name ?? string.Empty;
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !name.EndsWith(suffix, StringComparison.Ordinal))
            throw new ArgumentException("Known job modules require their Application assembly.");
        var module = name[prefix.Length..^suffix.Length];
        ModuleName = module.ToLowerInvariant();
        if (!BackgroundJobNames.Module(ModuleName)
            || contracts.GetName().Name != name + ".Contracts"
            || infrastructure.GetName().Name != prefix + module + ".Infrastructure")
            throw new ArgumentException("Job module contracts, application and infrastructure must name the same module.");
    }

    public string ModuleName { get; }
    public Assembly Contracts { get; }
    public Assembly Application { get; }
    public Assembly Infrastructure { get; }
}
