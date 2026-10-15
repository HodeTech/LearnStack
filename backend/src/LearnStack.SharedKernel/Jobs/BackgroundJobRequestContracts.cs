using System.Diagnostics.CodeAnalysis;

namespace LearnStack.SharedKernel.Jobs;

public enum BackgroundJobScope
{
    TenantWide,
    Organization,
}

public enum BackgroundJobExecutionProfile
{
    Transactional,
    External,
}

/// <summary>The exact business root admitted by the closed executor entry.</summary>
public enum BackgroundJobExecutionStage
{
    Transactional,
    Prepare,
    Finalize,
}

public enum BackgroundJobDefinitionReadiness
{
    Pending,
    Active,
}

/// <summary>Positive request classification; an unlisted request has no ordinary-job authority.</summary>
public enum BackgroundJobRequestClassification
{
    Ordinary,
    FrameworkReadOnly,
    FrameworkReadWrite,
    Platform,
    Privileged,
}

/// <summary>Immutable declaration for one exact request type, validated by the frozen registry.</summary>
public sealed class BackgroundJobRequestDescriptor(
    Type requestType, BackgroundJobRequestClassification classification, BackgroundJobScope? scope)
{
    public Type RequestType { get; } = requestType ?? throw new ArgumentNullException(nameof(requestType));
    public BackgroundJobRequestClassification Classification { get; } = classification;
    /// <summary>Ordinary requests declare scope; exact framework requests inherit the admitted route with null.</summary>
    public BackgroundJobScope? Scope { get; } = scope;
}

public interface IBackgroundJobDefinitionCatalog
{
    bool TryGet(string key, [NotNullWhen(true)] out BackgroundJobDefinitionDescriptor? definition);
    BackgroundJobDefinitionResolution Resolve(string? key);
}

public interface IBackgroundJobRequestCatalog
{
    IReadOnlyCollection<BackgroundJobRequestDescriptor> All { get; }
    bool TryGet(Type requestType, [NotNullWhen(true)] out BackgroundJobRequestDescriptor? descriptor);
}

/// <summary>Unknown and Pending are deployment deferrals; only an explicit tombstone is Retired.</summary>
public enum BackgroundJobDefinitionDisposition
{
    Active,
    Pending,
    Retired,
    Unknown,
    Invalid,
}

public sealed record BackgroundJobDefinitionResolution(
    BackgroundJobDefinitionDisposition Disposition, BackgroundJobDefinitionDescriptor? Definition);
