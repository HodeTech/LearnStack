namespace LearnStack.SharedKernel.Jobs;

/// <summary>Construction-free profile metadata; no request factory or executable delegate is admitted.</summary>
public sealed class BackgroundJobProfileRegistration
{
    private BackgroundJobProfileRegistration(BackgroundJobExecutionProfile profile, Type root,
        Type? finalize = null, Type? plan = null, Type? outcome = null, Type? externalPort = null)
    {
        Profile = profile;
        RootRequestType = root ?? throw new ArgumentNullException(nameof(root));
        FinalizeRequestType = finalize;
        PlanType = plan;
        OutcomeType = outcome;
        ExternalPortType = externalPort;
    }

    public BackgroundJobExecutionProfile Profile { get; }
    /// <summary>The transactional root or external prepare request.</summary>
    public Type RootRequestType { get; }
    public Type? FinalizeRequestType { get; }
    public Type? PlanType { get; }
    public Type? OutcomeType { get; }
    /// <summary>The module's Contracts port, never its Infrastructure implementation.</summary>
    public Type? ExternalPortType { get; }

    public static BackgroundJobProfileRegistration Transactional(Type rootRequestType) =>
        new(BackgroundJobExecutionProfile.Transactional, rootRequestType);

    public static BackgroundJobProfileRegistration External(Type prepareRequestType, Type finalizeRequestType,
        Type planType, Type outcomeType, Type externalPortType) =>
        new(BackgroundJobExecutionProfile.External, prepareRequestType,
            finalizeRequestType ?? throw new ArgumentNullException(nameof(finalizeRequestType)),
            planType ?? throw new ArgumentNullException(nameof(planType)),
            outcomeType ?? throw new ArgumentNullException(nameof(outcomeType)),
            externalPortType ?? throw new ArgumentNullException(nameof(externalPortType)));
}

/// <summary>Code-declared business metadata. Registration never grants submission authority.</summary>
public sealed class BackgroundJobDefinitionRegistration
{
    public BackgroundJobDefinitionRegistration(string key, Type declaringType, Type parameterType,
        BackgroundJobScope scope, BackgroundJobDefinitionReadiness readiness, string queue,
        BackgroundJobProfileRegistration profile, IEnumerable<Type>? nestedRequestTypes = null,
        string? capacityGroup = null)
    {
        Key = key ?? throw new ArgumentNullException(nameof(key));
        DeclaringType = declaringType ?? throw new ArgumentNullException(nameof(declaringType));
        ParameterType = parameterType ?? throw new ArgumentNullException(nameof(parameterType));
        Scope = scope;
        Readiness = readiness;
        Queue = queue ?? throw new ArgumentNullException(nameof(queue));
        Profile = profile ?? throw new ArgumentNullException(nameof(profile));
        NestedRequestTypes = Array.AsReadOnly((nestedRequestTypes ?? []).ToArray());
        CapacityGroup = capacityGroup;
    }

    public string Key { get; }
    public Type DeclaringType { get; }
    public Type ParameterType { get; }
    public BackgroundJobScope Scope { get; }
    public BackgroundJobDefinitionReadiness Readiness { get; }
    public string Queue { get; }
    public BackgroundJobProfileRegistration Profile { get; }
    public IReadOnlyList<Type> NestedRequestTypes { get; }
    public string? CapacityGroup { get; }
}

/// <summary>A frozen definition's module identity is derived from its declaring Application assembly.</summary>
public sealed class BackgroundJobDefinitionDescriptor
{
    public BackgroundJobDefinitionDescriptor(BackgroundJobDefinitionRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        const string prefix = "LearnStack.Modules.";
        const string suffix = ".Application";
        var assemblyName = registration.DeclaringType.Assembly.GetName().Name ?? string.Empty;
        if (!assemblyName.StartsWith(prefix, StringComparison.Ordinal)
            || !assemblyName.EndsWith(suffix, StringComparison.Ordinal))
            throw new ArgumentException("A job definition is declared in its module's Application assembly.", nameof(registration));

        OwnerModule = assemblyName[prefix.Length..^suffix.Length].ToLowerInvariant();
        Key = registration.Key;
        ParameterType = registration.ParameterType;
        Scope = registration.Scope;
        Profile = registration.Profile.Profile;
        Readiness = registration.Readiness;
        Queue = registration.Queue;
        CapacityGroup = registration.CapacityGroup;
        ProfileRegistration = registration.Profile;
        RootRequestTypes = Array.AsReadOnly(registration.Profile.FinalizeRequestType is { } finalize
            ? new[] { registration.Profile.RootRequestType, finalize }
            : [registration.Profile.RootRequestType]);
        NestedRequestTypes = Array.AsReadOnly(registration.NestedRequestTypes.ToArray());
    }

    public string Key { get; }
    public string OwnerModule { get; }
    public Type ParameterType { get; }
    public BackgroundJobScope Scope { get; }
    public BackgroundJobExecutionProfile Profile { get; }
    public BackgroundJobDefinitionReadiness Readiness { get; }
    public string Queue { get; }
    public string? CapacityGroup { get; }
    public BackgroundJobProfileRegistration ProfileRegistration { get; }
    public IReadOnlyList<Type> RootRequestTypes { get; }
    public IReadOnlyList<Type> NestedRequestTypes { get; }
}
