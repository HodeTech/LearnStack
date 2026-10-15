using Hangfire.PostgreSql;
using LearnStack.Infrastructure.Persistence;
using Npgsql;

namespace LearnStack.Infrastructure.Jobs;

/// <summary>Every vendor acquisition crosses the existing authority and physical-role guards.</summary>
public sealed class HangfireConnectionFactory(PrivilegedDataSource source) : IConnectionFactory
{
    private readonly PrivilegedDataSource _source = source ?? throw new ArgumentNullException(nameof(source));

    public NpgsqlConnection GetOrCreateConnection() => _source.OpenConnection();
}
