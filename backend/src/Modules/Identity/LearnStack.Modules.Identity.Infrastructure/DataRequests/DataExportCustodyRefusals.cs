namespace LearnStack.Modules.Identity.Infrastructure.DataRequests;

/// <summary>
/// Which custody refusals this process has already reported at critical severity, so that a stored chunk that cannot
/// be opened pages once per artifact and key label and its repeats are warnings
/// (<see href="../../../../../docs/runbooks/data-export.md">data export runbook</see>).
/// </summary>
/// <remarks>
/// A download reads every chunk of an artifact, and a subject retries; one lost key would otherwise page once per
/// chunk read. The memory is one per process (a singleton), holds identifiers and non-secret labels only, and is
/// bounded: past <see cref="Capacity"/> distinct pairs it is cleared, so a refusal reported long ago is reported
/// at critical severity again rather than the set growing without limit.
/// </remarks>
public sealed class DataExportCustodyRefusals
{
    /// <summary>The most distinct (artifact, key label) pairs remembered before the memory is cleared.</summary>
    public const int Capacity = 256;

    private readonly HashSet<(Guid ArtifactId, string KeyLabel)> _reported = [];
    private readonly Lock _gate = new();

    /// <summary>
    /// Whether this is the first refusal of the artifact under the label since the process started or the memory was
    /// last cleared; the pair is remembered either way.
    /// </summary>
    public bool IsFirst(Guid artifactId, string keyLabel)
    {
        ArgumentNullException.ThrowIfNull(keyLabel);
        var pair = (artifactId, keyLabel);
        lock (_gate)
        {
            if (_reported.Count >= Capacity && !_reported.Contains(pair))
            {
                _reported.Clear();
            }

            return _reported.Add(pair);
        }
    }

    public override string ToString() => nameof(DataExportCustodyRefusals);
}
