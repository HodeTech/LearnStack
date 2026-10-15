using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Frozen exact identities, positive request metadata and construction-free profile bindings.</summary>
public sealed class BackgroundJobDefinitionRegistry : IBackgroundJobDefinitionCatalog, IBackgroundJobRequestCatalog
{
    private readonly FrozenDictionary<string, BackgroundJobDefinitionDescriptor> _definitions;
    private readonly FrozenDictionary<Type, BackgroundJobRequestDescriptor> _requests;
    private readonly FrozenSet<string> _retired;

    private BackgroundJobDefinitionRegistry(
        FrozenDictionary<string, BackgroundJobDefinitionDescriptor> definitions,
        FrozenDictionary<Type, BackgroundJobRequestDescriptor> requests,
        FrozenSet<string> retired, BackgroundJobInfrastructureRegistry infrastructure)
    {
        _definitions = definitions;
        _requests = requests;
        _retired = retired;
        All = Array.AsReadOnly(requests.Values.OrderBy(request => request.RequestType.FullName, StringComparer.Ordinal).ToArray());
        Infrastructure = infrastructure;
        DefinitionKeys = Array.AsReadOnly(definitions.Keys.Order(StringComparer.Ordinal).ToArray());
        RetiredKeys = Array.AsReadOnly(retired.Order(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<string> DefinitionKeys { get; }
    public IReadOnlyCollection<BackgroundJobRequestDescriptor> All { get; }
    public IReadOnlyList<string> RetiredKeys { get; }
    public BackgroundJobInfrastructureRegistry Infrastructure { get; }

    /// <summary>P4 intentionally has no production business definitions or permitted job requests.</summary>
    public static BackgroundJobDefinitionRegistry CreateEmpty() => Create([], [], []);

    public static BackgroundJobDefinitionRegistry Create(
        IEnumerable<BackgroundJobDefinitionRegistration> definitions,
        IEnumerable<BackgroundJobRequestDescriptor> requests,
        IEnumerable<BackgroundJobModuleRegistration> knownModules,
        IEnumerable<string>? retiredKeys = null,
        BackgroundJobInfrastructureRegistry? infrastructure = null)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(knownModules);
        infrastructure ??= BackgroundJobInfrastructureRegistry.CreateDefault();
        var modules = knownModules.ToArray();
        if (modules.Any(module => module is null) || modules.Select(module => module.ModuleName).Distinct(StringComparer.Ordinal).Count() != modules.Length)
            throw Invalid("Known job modules must be distinct and non-null.");

        var requestMap = new Dictionary<Type, BackgroundJobRequestDescriptor>();
        foreach (var request in requests)
        {
            if (request is null) throw Invalid("A job request declaration cannot be null.");
            ValidateRequest(request, modules);
            if (!requestMap.TryAdd(request.RequestType, request))
                throw Invalid("Each exact job request type has one immutable classification.");
        }

        var byKey = new Dictionary<string, BackgroundJobDefinitionDescriptor>(StringComparer.Ordinal);
        foreach (var registration in definitions)
        {
            if (registration is null) throw Invalid("A job definition cannot be null.");
            var module = modules.SingleOrDefault(candidate => candidate.Application == registration.DeclaringType.Assembly)
                ?? throw Invalid("A job definition belongs to a known owning Application assembly.");
            var definition = new BackgroundJobDefinitionDescriptor(registration);
            if (!BackgroundJobNames.Definition(definition.Key)
                || !definition.Key.StartsWith(module.ModuleName + ".", StringComparison.Ordinal)
                || !Enum.IsDefined(definition.Scope) || !Enum.IsDefined(definition.Readiness)
                || !byKey.TryAdd(definition.Key, definition))
                throw Invalid("Definitions require distinct module.job_name.vN keys and declared scope/readiness.");
            if (registration.DeclaringType.ContainsGenericParameters || registration.DeclaringType.IsInterface
                || !registration.DeclaringType.IsClass)
                throw Invalid("A job definition must name a closed Application declaration type.");
            if (definition.ParameterType != typeof(BackgroundJobOccurrenceParameters)
                && definition.ParameterType.Assembly != module.Contracts)
                throw Invalid("Job parameters belong to the owning Application.Contracts assembly.");
            BackgroundJobPayloadContract.ValidateShape(definition.ParameterType, BackgroundJobPayloadKind.Parameters);
            ValidateProfile(definition, module, infrastructure);
            var allowed = definition.RootRequestTypes.Concat(definition.NestedRequestTypes).ToArray();
            if (allowed.Distinct().Count() != allowed.Length)
                throw Invalid("A definition's root and nested request types must be distinct.");
            foreach (var type in allowed)
            {
                if (!requestMap.TryGetValue(type, out var request)
                    || request.Classification != BackgroundJobRequestClassification.Ordinary
                    || request.Scope != definition.Scope)
                    throw Invalid("Every exact definition request needs matching positive Ordinary scope metadata.");
                if (definition.RootRequestTypes.Contains(type) ? type.Assembly != module.Application
                    : type.Assembly != module.Application && !modules.Any(candidate => candidate.Contracts == type.Assembly))
                    throw Invalid("Roots belong to the owning Application; nested requests use its Application or known module contracts.");
            }
        }

        var retired = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in retiredKeys ?? [])
            if (!BackgroundJobNames.Definition(key) || byKey.ContainsKey(key) || !retired.Add(key))
                throw Invalid("Retired job identities must be valid, unique tombstones distinct from live definitions.");

        return new(byKey.ToFrozenDictionary(StringComparer.Ordinal), requestMap.ToFrozenDictionary(),
            retired.ToFrozenSet(StringComparer.Ordinal), infrastructure);
    }

    public bool TryGet(string key, [NotNullWhen(true)] out BackgroundJobDefinitionDescriptor? definition) =>
        _definitions.TryGetValue(key, out definition);

    public bool TryGet(Type requestType, [NotNullWhen(true)] out BackgroundJobRequestDescriptor? descriptor) =>
        _requests.TryGetValue(requestType, out descriptor);

    public BackgroundJobDefinitionResolution Resolve(string? key)
    {
        if (!BackgroundJobNames.Definition(key)) return new(BackgroundJobDefinitionDisposition.Invalid, null);
        if (_definitions.TryGetValue(key!, out var definition))
            return new(definition.Readiness == BackgroundJobDefinitionReadiness.Active
                ? BackgroundJobDefinitionDisposition.Active : BackgroundJobDefinitionDisposition.Pending, definition);
        return new(_retired.Contains(key!) ? BackgroundJobDefinitionDisposition.Retired : BackgroundJobDefinitionDisposition.Unknown, null);
    }

    private static void ValidateRequest(BackgroundJobRequestDescriptor request, BackgroundJobModuleRegistration[] modules)
    {
        var type = request.RequestType;
        var framework = request.Classification is BackgroundJobRequestClassification.FrameworkReadOnly
            or BackgroundJobRequestClassification.FrameworkReadWrite;
        var contracts = type.GetInterfaces().Where(candidate => candidate.IsGenericType
            && candidate.GetGenericTypeDefinition() == typeof(IRequest<>)).ToArray();
        if (!type.IsClass || type.IsAbstract || type.ContainsGenericParameters || contracts.Length != 1
            || !typeof(IResultBase).IsAssignableFrom(contracts[0].GenericTypeArguments[0])
            || !Enum.IsDefined(request.Classification)
            || (framework ? request.Scope is not null : request.Scope is not { } scope || !Enum.IsDefined(scope)))
            throw Invalid("Request metadata must name one exact concrete Result-bearing MediatR request and declared classification/scope.");
        var knownModule = modules.Any(module => module.Application == type.Assembly || module.Contracts == type.Assembly);
        if (framework ? type.Assembly != typeof(BackgroundJobDefinitionRegistry).Assembly
                && type.Assembly != typeof(LearnStack.Application.AssemblyMarker).Assembly : !knownModule)
            throw Invalid("Ordinary requests belong to known modules; framework metadata belongs only to the core runtime.");
        if (request.Classification is BackgroundJobRequestClassification.Ordinary || framework)
        {
            if (typeof(IProvisionsTenant).IsAssignableFrom(type)
                || type.IsDefined(typeof(AllowsUnresolvedTenantContextAttribute), inherit: false)
                || type.IsDefined(typeof(PublicSurfaceAttribute), inherit: false))
                throw Invalid("Job metadata cannot relabel provisioning, unresolved or public-host authority as ordinary work.");
        }
    }

    private static void ValidateProfile(BackgroundJobDefinitionDescriptor definition,
        BackgroundJobModuleRegistration module, BackgroundJobInfrastructureRegistry infrastructure)
    {
        var profile = definition.ProfileRegistration;
        var poolKind = definition.Profile switch
        {
            BackgroundJobExecutionProfile.Transactional => BackgroundJobWorkerPoolKind.Transactional,
            BackgroundJobExecutionProfile.External => BackgroundJobWorkerPoolKind.External,
            _ => throw Invalid("A job requires a declared execution profile."),
        };
        if (!infrastructure.TryGetPoolForQueue(definition.Queue, out var pool) || pool.Kind != poolKind)
            throw Invalid("A job queue must belong to its execution profile's independent worker pool.");
        if (definition.CapacityGroup is { } group
            && (!infrastructure.CapacityGroups.ContainsKey(group) || !group.StartsWith(module.ModuleName + ".", StringComparison.Ordinal)))
            throw Invalid("A job capacity group must be declared by its owning module.");
        if (profile.RootRequestType.GetConstructor([definition.ParameterType]) is null)
            throw Invalid("Job roots and prepare requests require the exact registered parameter constructor.");
        if (definition.Profile == BackgroundJobExecutionProfile.Transactional)
        {
            if (!typeof(IRequest<Result<None>>).IsAssignableFrom(profile.RootRequestType))
                throw Invalid("A transactional job root returns Result<None>.");
            return;
        }

        if (definition.CapacityGroup is null || profile.PlanType!.Assembly != module.Contracts
            || profile.OutcomeType!.Assembly != module.Contracts || profile.ExternalPortType!.Assembly != module.Contracts
            || !infrastructure.TryGetAdapter(profile.ExternalPortType, out _))
            throw Invalid("External jobs require their module's typed plan/outcome/port, a concrete adapter binding and a capacity group.");
        BackgroundJobPayloadContract.ValidateShape(profile.PlanType, BackgroundJobPayloadKind.Plan);
        BackgroundJobPayloadContract.ValidateShape(profile.OutcomeType, BackgroundJobPayloadKind.Outcome);
        var expectedPort = typeof(IBackgroundJobExternalOperation<,>).MakeGenericType(profile.PlanType, profile.OutcomeType);
        var prepareResult = typeof(IRequest<>).MakeGenericType(typeof(Result<>).MakeGenericType(profile.PlanType));
        var finalization = typeof(BackgroundJobFinalization<,>).MakeGenericType(profile.PlanType, profile.OutcomeType);
        if (!expectedPort.IsAssignableFrom(profile.ExternalPortType)
            || !prepareResult.IsAssignableFrom(profile.RootRequestType)
            || !typeof(IRequest<Result<None>>).IsAssignableFrom(profile.FinalizeRequestType)
            || profile.FinalizeRequestType!.GetConstructor([definition.ParameterType, finalization]) is null)
            throw Invalid("External profiles bind exact typed prepare, Execute/Reconcile and finalize contracts.");
    }

    private static ArgumentException Invalid(string message) => new(message);
}
