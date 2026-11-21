using System.Diagnostics;
using System.Diagnostics.Metrics;
using Npgsql;

namespace LearnStack.Infrastructure.Persistence;

public enum IdentityLockKind { Subject = 1, User = 2, Tenant = 3, Authority = 4 }

/// <summary>Closed process signals; no scope, SQL, exception or person value is a dimension.</summary>
public static class IdentityLockObservation
{
    public const string MeterName = "learnstack.identity";
    private static readonly Meter Meter = new(MeterName);
    private static readonly Histogram<double> Wait = Meter.CreateHistogram<double>(
        "learnstack_identity_lock_wait_seconds", "s");
    private static readonly Counter<long> Refusals = Meter.CreateCounter<long>(
        "learnstack_identity_lock_refusals_total", "1");

    internal static void Complete(IdentityLockKind kind, long started, Exception? failure = null)
    {
        var outcome = failure switch
        {
            null => "acquired",
            OperationCanceledException => "cancelled",
            PostgresException { SqlState: PostgresErrorCodes.LockNotAvailable } => "timeout",
            NpgsqlException { InnerException: TimeoutException } => "timeout",
            _ => "fault",
        };
        Wait.Record(Stopwatch.GetElapsedTime(started).TotalSeconds,
            new("lock_kind", Name(kind)), new("outcome", outcome));
        if (failure is not null)
            Refusals.Add(1, new("lock_kind", Name(kind)), new("reason", outcome));
    }

    /// <summary>A creation that waited out a rebind and then read an issuer other than its own.</summary>
    internal static void AuthorityChanged() =>
        Refusals.Add(1, new("lock_kind", Name(IdentityLockKind.Authority)), new("reason", "mismatch"));

    /// <summary>Called once by the owning adapter when a real guard rejects its lock plan.</summary>
    public static void PlanFault(IdentityLockKind kind) =>
        Refusals.Add(1, new("lock_kind", Name(kind)), new("reason", "fault"));

    private static string Name(IdentityLockKind kind) => kind switch
    {
        IdentityLockKind.Subject => "subject",
        IdentityLockKind.User => "user",
        IdentityLockKind.Tenant => "tenant",
        IdentityLockKind.Authority => "authority",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}
