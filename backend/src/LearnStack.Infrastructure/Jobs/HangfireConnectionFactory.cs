using Hangfire.PostgreSql;
using LearnStack.Infrastructure.Persistence;
using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Every vendor acquisition crosses the existing authority and physical-role guards.</summary>
public sealed class HangfireConnectionFactory(PrivilegedDataSource source, BackgroundJobRuntimeGate? gate = null) : IConnectionFactory
{
    private readonly PrivilegedDataSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public NpgsqlConnection GetOrCreateConnection()
    {
        if (gate?.TryAdmitStorage() == false)
            throw new InvalidOperationException("The job storage acquisition gate is closed.");
        return _source.OpenConnection();
    }
}
