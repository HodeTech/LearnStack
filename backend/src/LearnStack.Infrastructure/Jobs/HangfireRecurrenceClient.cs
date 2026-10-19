using System.Globalization;
using Hangfire;
using Hangfire.Common;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Only finite recurrence hints cross the pinned public recurring manager.</summary>
internal sealed class HangfireRecurrenceClient(JobStorage storage, IJobFilterProvider filters)
{
    private readonly RecurringJobManager _manager = new(storage, filters);

    internal void Register(BackgroundJobRecurrenceDescriptor manifest)
    {
        var job = new Job(typeof(HangfireRecurrenceTick), HangfireInvocationContract.TickMethod,
            [manifest.Facts.Key, manifest.Facts.Revision], "control");
        _manager.AddOrUpdate(manifest.Facts.Key, job, "* * * * *",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc, MisfireHandling = MisfireHandlingMode.Relaxed });
    }
}

/// <summary>One actual native thread and its startup task stay owned until both positively return.</summary>
internal sealed class BackgroundJobNativeRecurrenceOperation
{
    private readonly HangfireRecurrenceClient _client;
    private readonly BackgroundJobRuntimeGate _runtime;
    private readonly TaskCompletionSource<Task> _published = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion;
    private readonly Thread _thread;
    private readonly Task _observation;
    private readonly TimeProvider _time;
    private int _stopped, _scheduled;

    internal BackgroundJobNativeRecurrenceOperation(BackgroundJobRecurrenceDescriptor manifest,
        HangfireRecurrenceClient client, BackgroundJobRuntimeGate runtime, TimeProvider time)
    {
        Manifest = manifest;
        _client = client;
        _runtime = runtime;
        _time = time;
        StartedAt = time.GetTimestamp();
        _completion = new(this, TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(ExecuteNative) { IsBackground = true, Name = "learnstack-recurrence-hint" };
        _observation = ObserveAsync();
    }

    internal BackgroundJobRecurrenceDescriptor Manifest { get; }
    internal long StartedAt { get; }
    internal Task Completion => _completion.Task;
    internal bool? Result => _result.Task.IsCompletedSuccessfully ? _result.Task.Result : null;

    internal void RequestStop() => Volatile.Write(ref _stopped, 1);

    internal void Start()
    {
        if (Interlocked.Exchange(ref _scheduled, 1) != 0)
            throw new InvalidOperationException("Native recurrence operations start once.");
        try
        {
            Task startup;
            if (ExecutionContext.IsFlowSuppressed()) startup = Task.Run(StartThread);
            else { using (ExecutionContext.SuppressFlow()) startup = Task.Run(StartThread); }
            _published.TrySetResult(startup);
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        { RequestStop(); } // An unpublished scheduling attempt stays retained.
    }

    private void StartThread()
    {
        if (Volatile.Read(ref _stopped) != 0 || !_runtime.TryAdmit()) { _result.TrySetResult(null); return; }
        try
        {
            if (ExecutionContext.IsFlowSuppressed()) _thread.Start();
            else { using (ExecutionContext.SuppressFlow()) _thread.Start(); }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        {
            if (!_thread.IsAlive && (_thread.ThreadState & ThreadState.Unstarted) != 0)
                _result.TrySetResult(false);
        }
    }

    private void ExecuteNative()
    {
        bool? result = null;
        var culture = CultureInfo.CurrentCulture;
        var uiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
            if (Volatile.Read(ref _stopped) == 0 && _runtime.TryAdmit())
            {
                _client.Register(Manifest);
                result = true;
            }
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure)) { result = false; }
        finally
        {
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = uiCulture;
            _result.TrySetResult(result);
        }
    }

    private async Task ObserveAsync()
    {
        try
        {
            var startup = await _published.Task.ConfigureAwait(false);
            await startup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            await _result.Task.ConfigureAwait(false);
            while (_thread.IsAlive)
                await Task.Delay(TimeSpan.FromMilliseconds(1), _time, CancellationToken.None).ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (Exception failure) when (BackgroundJobControlResources.Catchable(failure))
        { } // Neither a failed observer nor a finite wait proves physical return.
    }
}
