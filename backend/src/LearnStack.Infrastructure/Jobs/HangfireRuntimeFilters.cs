using Hangfire;
using Hangfire.Common;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Snapshot pinned globals and apply stock filter precedence to wrapper attributes.</summary>
internal sealed class HangfireRuntimeFilters : IJobFilterProvider
{
    private static readonly object Gate = new();
    private readonly JobFilterProviderCollection _filters;

    private HangfireRuntimeFilters(JobFilter[] globals) => _filters = new(
        new FrozenGlobalFilters(globals), new JobFilterAttributeFilterProvider());

    internal static HangfireRuntimeFilters Create()
    {
        lock (Gate)
        {
            var before = GlobalJobFilters.Filters.ToArray();
            var required = new[]
            {
                typeof(CaptureCultureAttribute), typeof(AutomaticRetryAttribute),
                typeof(ContinuationsSupportAttribute),
            };
            var retryOrder = new AutomaticRetryAttribute().Order;
            if (required.Any(type => before.Count(filter => filter.Instance?.GetType() == type) != 1)
                || before.Count(filter => filter.Instance?.GetType() == typeof(StatisticsHistoryAttribute)) > 1
                || before.Any(filter => filter.Instance is AutomaticRetryAttribute && filter.Order != retryOrder)
                || before.Any(filter => filter.Instance is null
                    || !required.Contains(filter.Instance.GetType())
                        && filter.Instance.GetType() != typeof(StatisticsHistoryAttribute)))
                throw new HangfireStorageRefusedException();

            GlobalJobFilters.Filters.Remove<StatisticsHistoryAttribute>();
            var selected = GlobalJobFilters.Filters.ToArray();
            if (selected.Length != required.Length
                || required.Any(type => selected.Count(filter => filter.Instance.GetType() == type) != 1))
                throw new HangfireStorageRefusedException();
            return new HangfireRuntimeFilters(selected);
        }
    }

    public IEnumerable<JobFilter> GetFilters(Job job)
    {
        ArgumentNullException.ThrowIfNull(job);
        return _filters.GetFilters(job);
    }

    private sealed class FrozenGlobalFilters(JobFilter[] globals) : IJobFilterProvider
    {
        public IEnumerable<JobFilter> GetFilters(Job job) => globals;
    }
}
