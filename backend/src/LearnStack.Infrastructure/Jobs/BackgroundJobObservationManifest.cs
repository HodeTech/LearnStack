using System.Collections.Frozen;
using System.Collections.Immutable;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using NpgsqlTypes;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Construction-free finite metadata; it carries no callback, payload or execution authority.</summary>
internal sealed class BackgroundJobObservationManifest
{
    private sealed record Definition(string Key, string Profile, string Queue, string? CapacityGroup,
        bool TenantWide, bool Active, bool OccurrenceInput, string Owner);
    private sealed record Recurrence(string Key, long Revision, string Hash);

    private readonly ImmutableArray<Definition> _definitions;
    private readonly ImmutableArray<Recurrence> _recurrences;
    private readonly ImmutableArray<string> _retired;
    private readonly FrozenSet<string> _groups;
    private readonly FrozenSet<string> _recurrenceKeys;

    private BackgroundJobObservationManifest(ImmutableArray<Definition> definitions,
        ImmutableArray<Recurrence> recurrences, ImmutableArray<string> retired, FrozenSet<string> groups)
    {
        _definitions = definitions;
        _recurrences = recurrences;
        _retired = retired;
        _groups = groups;
        _recurrenceKeys = recurrences.Select(item => item.Key).ToFrozenSet(StringComparer.Ordinal);
    }

    internal int MaximumRows => checked(3 + 4 * 5 + 4 + _groups.Count + 2 + 3 * (_recurrences.Length + 2));

    internal static BackgroundJobObservationManifest Create(BackgroundJobDefinitionRegistry definitions,
        BackgroundJobRecurrenceRegistry recurrences)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        ArgumentNullException.ThrowIfNull(recurrences);
        var declared = definitions.DefinitionKeys.Select(key =>
        {
            if (!definitions.TryGet(key, out var definition))
                throw new InvalidOperationException("The frozen observation definition is missing.");
            return new Definition(key,
                definition.Profile == BackgroundJobExecutionProfile.External ? "external" : "transactional",
                definition.Queue, definition.CapacityGroup, definition.Scope == BackgroundJobScope.TenantWide,
                definition.Readiness == BackgroundJobDefinitionReadiness.Active,
                definition.ParameterType == typeof(BackgroundJobOccurrenceParameters), definition.OwnerModule);
        }).ToImmutableArray();
        var manifests = recurrences.Keys.Select(key =>
        {
            if (!recurrences.TryGet(key, out var recurrence))
                throw new InvalidOperationException("The frozen observation recurrence is missing.");
            return new Recurrence(key, recurrence.Registration.Revision, recurrence.ManifestHash);
        }).ToImmutableArray();
        return new(declared, manifests, definitions.RetiredKeys.ToImmutableArray(),
            definitions.Infrastructure.CapacityGroups.Keys.ToFrozenSet(StringComparer.Ordinal));
    }

    internal void Bind(NpgsqlCommand command, IReadOnlyCollection<Guid> retainedJobs)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(retainedJobs);
        if (retainedJobs.Any(id => id == Guid.Empty) || retainedJobs.Distinct().Count() != retainedJobs.Count)
            throw new ArgumentException("Observation exclusions require distinct actual job identities.", nameof(retainedJobs));
        Text("d_keys", _definitions.Select(item => item.Key));
        Text("d_profiles", _definitions.Select(item => item.Profile));
        Text("d_queues", _definitions.Select(item => item.Queue));
        Text("d_groups", _definitions.Select(item => item.CapacityGroup));
        Flags("d_tenantwide", _definitions.Select(item => item.TenantWide));
        Flags("d_active", _definitions.Select(item => item.Active));
        Flags("d_occurrence", _definitions.Select(item => item.OccurrenceInput));
        Text("d_owners", _definitions.Select(item => item.Owner));
        Text("r_keys", _recurrences.Select(item => item.Key));
        command.Parameters.AddWithValue("r_revisions", NpgsqlDbType.Array | NpgsqlDbType.Bigint,
            _recurrences.Select(item => item.Revision).ToArray());
        Text("r_hashes", _recurrences.Select(item => item.Hash));
        Text("retired", _retired);
        Text("capacity_groups", _groups);
        command.Parameters.AddWithValue("retained_jobs", NpgsqlDbType.Array | NpgsqlDbType.Uuid, retainedJobs.ToArray());

        void Text(string name, IEnumerable<string?> values) =>
            command.Parameters.AddWithValue(name, NpgsqlDbType.Array | NpgsqlDbType.Text, values.ToArray());
        void Flags(string name, IEnumerable<bool> values) =>
            command.Parameters.AddWithValue(name, NpgsqlDbType.Array | NpgsqlDbType.Boolean, values.ToArray());
    }

    internal bool Allows(BackgroundJobObservationPoint point) => point.Family switch
    {
        BackgroundJobObservationFamily.Queue => point.Label is "transactional" or "external" or "invalid" && point.Code == 0,
        BackgroundJobObservationFamily.Deferred => IsArtifact(point.Label) && point.Code is >= 1 and <= 5,
        BackgroundJobObservationFamily.Quarantine => IsArtifact(point.Label) && point.Code == 0,
        BackgroundJobObservationFamily.ReservedCapacity => (_groups.Contains(point.Label)
            || point.Label is "unknown" or "invalid") && point.Code == 0,
        BackgroundJobObservationFamily.FanoutPending => (_recurrenceKeys.Contains(point.Label)
            || point.Label is "unknown" or "invalid") && point.Code is >= 1 and <= 3,
        _ => false,
    };

    internal static bool IsArtifact(string label) => label is "job" or "recurrence" or "occurrence" or "recipient";
}
