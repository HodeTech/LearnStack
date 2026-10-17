namespace LearnStack.Infrastructure.Jobs;

internal sealed partial class BackgroundJobControl
{
    internal void BindRecurrenceClient(HangfireRecurrenceClient client) => _recurrence.BindNativeClient(client);

    // Only a coalesced memory hint crosses the wrapper. Durable polling owns SQL.
    internal void SignalRecurrenceTick(string key, long revision) => _recurrence.SignalTick(key, revision);
}
