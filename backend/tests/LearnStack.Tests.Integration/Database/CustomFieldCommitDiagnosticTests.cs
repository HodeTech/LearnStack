using System.Collections.Concurrent;
using FluentAssertions;
using LearnStack.Infrastructure.Persistence;
using LearnStack.SharedKernel.Audit;
using Microsoft.Extensions.Logging;
using Npgsql;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Formatting.Json;
using Xunit;

namespace LearnStack.Tests.Integration.Database;

[Trait(RequiresDocker.Key, RequiresDocker.Value)]
public sealed class CustomFieldCommitDiagnosticTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Deferred_provider_fault_is_excluded_from_serialized_driver_logs_only_during_opt_in()
    {
        const string canary = "deferred-custom-field-value-62471";
        var sink = new JsonSink();
        using var logger = new LoggerConfiguration().MinimumLevel.Debug().WriteTo.Sink(sink).CreateLogger();
        using var factory = new SerilogLoggerFactory(logger);
        var wrapped = CustomFieldCommitDiagnostics.Wrap(factory);
        var driver = wrapped.CreateLogger("Npgsql.Command");
        var sibling = wrapped.CreateLogger("LearnStack.Unrelated");
        var settings = new NpgsqlConnectionStringBuilder(postgres.MigrationConnectionString) { IncludeErrorDetail = true };
        var builder = new NpgsqlDataSourceBuilder(settings.ConnectionString);
        builder.UseLoggerFactory(wrapped);
        await using var source = builder.Build();
        await using var connection = await source.OpenConnectionAsync();
        await using (var setup = new NpgsqlCommand("CREATE TABLE diagnostic_canary (value text, CONSTRAINT diagnostic_unique UNIQUE (value) DEFERRABLE INITIALLY DEFERRED)", connection))
            await setup.ExecuteNonQueryAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var insert = new NpgsqlCommand("INSERT INTO diagnostic_canary VALUES (@value), (@value)", connection, transaction))
        {
            insert.Parameters.AddWithValue("value", canary);
            await insert.ExecuteNonQueryAsync();
        }

        Before(driver, null);
        PostgresException failure;
        using (CustomFieldCommitDiagnostics.Enter())
        {
            failure = await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            failure.Detail.Should().Contain(canary, "the database must actually return the authored value at COMMIT");
            Failure(driver, failure);
            Unrelated(sibling, null);
        }
        using (ConfidentialAuditDiagnostics.Enter())
        {
            Failure(driver, failure);
            Unrelated(sibling, null);
        }
        After(driver, null);

        var serialized = string.Join('\n', sink.Events);
        serialized.Should().Contain("driver-before-safe-control").And.Contain("driver-after-safe-control")
            .And.Contain("unrelated-inside-safe-control");
        serialized.Should().NotContain(canary).And.NotContain("driver-failure");
    }

    private static readonly Action<Microsoft.Extensions.Logging.ILogger, Exception?> Before = LoggerMessage.Define(
        LogLevel.Information, new EventId(1, "Before"), "driver-before-safe-control");
    private static readonly Action<Microsoft.Extensions.Logging.ILogger, Exception?> After = LoggerMessage.Define(
        LogLevel.Information, new EventId(2, "After"), "driver-after-safe-control");
    private static readonly Action<Microsoft.Extensions.Logging.ILogger, Exception?> Unrelated = LoggerMessage.Define(
        LogLevel.Information, new EventId(3, "Unrelated"), "unrelated-inside-safe-control");
    private static readonly Action<Microsoft.Extensions.Logging.ILogger, Exception?> Failure = LoggerMessage.Define(
        LogLevel.Error, new EventId(4, "Failure"), "driver-failure");

    private sealed class JsonSink : ILogEventSink
    {
        private readonly JsonFormatter _formatter = new(renderMessage: true);
        public ConcurrentQueue<string> Events { get; } = new();
        public void Emit(LogEvent logEvent)
        {
            using var writer = new StringWriter(System.Globalization.CultureInfo.InvariantCulture);
            _formatter.Format(logEvent, writer);
            Events.Enqueue(writer.ToString());
        }
    }
}
