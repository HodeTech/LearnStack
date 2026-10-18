using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using LearnStack.Infrastructure.Jobs;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Only this actual host's named job source; no unrelated parallel host can satisfy a premise.</summary>
internal sealed class BackgroundJobTraceProbe : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentQueue<Activity> _started = new(), _stopped = new();

    internal BackgroundJobTraceProbe(BackgroundJobObservation observation, Action<Activity>? started = null)
    {
        var source = (ActivitySource)typeof(BackgroundJobObservation).GetField("_activities", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(observation)!;
        _listener = new()
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => { _started.Enqueue(activity); started?.Invoke(activity); },
            ActivityStopped = activity => _stopped.Enqueue(activity),
        };
        ActivitySource.AddActivityListener(_listener);
    }

    internal Activity[] Started => _started.ToArray();
    internal Activity[] Stopped => _stopped.ToArray();
    public void Dispose() => _listener.Dispose();
}
