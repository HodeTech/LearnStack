using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Frozen manifest and its deterministic fingerprint; runtime activation remains monotonic durable state.</summary>
public sealed class BackgroundJobRecurrenceDescriptor
{
    internal BackgroundJobRecurrenceDescriptor(BackgroundJobRecurrenceRegistration registration, BackgroundJobScope scope)
    {
        Registration = registration;
        Scope = scope;
        var canonical = string.Join('\n', registration.Key,
            registration.Revision.ToString(CultureInfo.InvariantCulture),
            registration.ActivatesAt.UtcTicks.ToString(CultureInfo.InvariantCulture), registration.Target.Value,
            ((int)registration.Schedule).ToString(CultureInfo.InvariantCulture),
            registration.Anchor.UtcTicks.ToString(CultureInfo.InvariantCulture),
            registration.Period.Ticks.ToString(CultureInfo.InvariantCulture),
            registration.Jitter.Ticks.ToString(CultureInfo.InvariantCulture),
            ((int)registration.RecipientPolicy).ToString(CultureInfo.InvariantCulture),
            ((int)registration.MisfirePolicy).ToString(CultureInfo.InvariantCulture),
            ((int)scope).ToString(CultureInfo.InvariantCulture));
        ManifestHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public BackgroundJobRecurrenceRegistration Registration { get; }
    public BackgroundJobScope Scope { get; }
    public string ManifestHash { get; }
}

/// <summary>Construction-free recurrence metadata. Unknown lookups preserve work for a compatible deployment.</summary>
public sealed class BackgroundJobRecurrenceRegistry
{
    private readonly FrozenDictionary<string, BackgroundJobRecurrenceDescriptor> _recurrences;

    private BackgroundJobRecurrenceRegistry(FrozenDictionary<string, BackgroundJobRecurrenceDescriptor> recurrences)
    {
        _recurrences = recurrences;
        Keys = Array.AsReadOnly(recurrences.Keys.Order(StringComparer.Ordinal).ToArray());
    }

    public IReadOnlyList<string> Keys { get; }

    public static BackgroundJobRecurrenceRegistry CreateEmpty() => Create([], BackgroundJobDefinitionRegistry.CreateEmpty(), []);

    public static BackgroundJobRecurrenceRegistry Create(
        IEnumerable<BackgroundJobRecurrenceRegistration> registrations,
        IBackgroundJobDefinitionCatalog definitions,
        IEnumerable<BackgroundJobModuleRegistration> knownModules)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(knownModules);
        var modules = knownModules.ToArray();
        if (modules.Any(module => module is null)
            || modules.Select(module => module.ModuleName).Distinct(StringComparer.Ordinal).Count() != modules.Length)
            throw new ArgumentException("Recurrence modules must be distinct and non-null.");
        var byKey = new Dictionary<string, BackgroundJobRecurrenceDescriptor>(StringComparer.Ordinal);
        foreach (var registration in registrations)
        {
            var module = registration is null ? null
                : modules.SingleOrDefault(candidate => candidate.Application == registration.DeclaringType.Assembly);
            if (registration is null || module is null || !BackgroundJobNames.ModuleResource(registration.Key)
                || !registration.Key.StartsWith(module.ModuleName + ".", StringComparison.Ordinal)
                || registration.Revision <= 0 || registration.ActivatesAt == default || registration.Anchor == default
                || registration.ActivatesAt.Offset != TimeSpan.Zero || registration.Anchor.Offset != TimeSpan.Zero
                || !Enum.IsDefined(registration.Schedule) || !Enum.IsDefined(registration.RecipientPolicy)
                || registration.MisfirePolicy != BackgroundJobMisfirePolicy.CoalesceLatest
                || registration.Period < TimeSpan.FromMinutes(1) || registration.Period > TimeSpan.FromDays(366)
                || registration.Schedule == BackgroundJobRecurrenceSchedule.Daily && registration.Period != TimeSpan.FromDays(1)
                || registration.Jitter < TimeSpan.Zero || registration.Jitter > registration.Period
                || registration.Jitter > TimeSpan.FromHours(2))
                throw new ArgumentException("A recurrence requires a valid module-owned UTC interval/daily manifest with bounded jitter.");
            if (registration.Target.Value is null || !definitions.TryGet(registration.Target.Value, out var target)
                || target.OwnerModule != module.ModuleName || target.ParameterType != typeof(BackgroundJobOccurrenceParameters)
                || target.Scope != (registration.RecipientPolicy == BackgroundJobRecipientPolicy.ServingOrganizations
                    ? BackgroundJobScope.Organization : BackgroundJobScope.TenantWide))
                throw new ArgumentException("A recurrence binds its module's exact occurrence-input definition and matching recipient scope.");
            if (!byKey.TryAdd(registration.Key, new BackgroundJobRecurrenceDescriptor(registration, target.Scope)))
                throw new ArgumentException("A binary declares one revision per recurrence key.");
        }
        return new(byKey.ToFrozenDictionary(StringComparer.Ordinal));
    }

    public bool TryGet(string key, [NotNullWhen(true)] out BackgroundJobRecurrenceDescriptor? descriptor) =>
        _recurrences.TryGetValue(key, out descriptor);
}
