using FluentAssertions;
using LearnStack.Infrastructure.Observability.Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Observability;

/// <summary>
/// RedactSensitiveFieldsEnricher behaviour (review-4): redacts sensitive
/// top-level AND nested properties, while leaving ordinary fields (and
/// substring-collision names like ClassName) untouched.
/// </summary>
public sealed class RedactSensitiveFieldsEnricherTests
{
    private const string Redacted = SensitiveTokenCatalog_RedactedValue;
    private const string SensitiveTokenCatalog_RedactedValue = "***REDACTED***";

    [Fact]
    public void Redacts_Top_Level_Sensitive_Property()
    {
        var logEvent = CreateEvent(
            new LogEventProperty("Password", new ScalarValue("hunter2")),
            new LogEventProperty("UserName", new ScalarValue("alice")));

        Enrich(logEvent);

        Scalar(logEvent, "Password").Should().Be(Redacted);
        Scalar(logEvent, "UserName").Should().Be("alice");
    }

    [Fact]
    public void Does_Not_Redact_Substring_Collision_Names()
    {
        var logEvent = CreateEvent(
            new LogEventProperty("ClassName", new ScalarValue("OrderService")),
            new LogEventProperty("BusinessName", new ScalarValue("Acme")));

        Enrich(logEvent);

        Scalar(logEvent, "ClassName").Should().Be("OrderService");
        Scalar(logEvent, "BusinessName").Should().Be("Acme");
    }

    [Fact]
    public void Redacts_Nested_Sensitive_Property_In_Destructured_Object()
    {
        var user = new StructureValue(
        [
            new LogEventProperty("Id", new ScalarValue("u-1")),
            new LogEventProperty("Password", new ScalarValue("hunter2")),
            new LogEventProperty("Tckn", new ScalarValue("12345678901")),
        ]);
        var logEvent = CreateEvent(new LogEventProperty("User", user));

        Enrich(logEvent);

        var redactedUser = (StructureValue)logEvent.Properties["User"];
        ScalarOf(redactedUser, "Id").Should().Be("u-1");
        ScalarOf(redactedUser, "Password").Should().Be(Redacted);
        ScalarOf(redactedUser, "Tckn").Should().Be(Redacted);
    }

    [Fact]
    public void Leaves_Clean_Event_Unchanged_By_Reference()
    {
        var inner = new ScalarValue("plain");
        var logEvent = CreateEvent(new LogEventProperty("UserName", inner));

        Enrich(logEvent);

        // No sensitive data anywhere → the original value instance is retained.
        logEvent.Properties["UserName"].Should().BeSameAs(inner);
    }

    private static void Enrich(LogEvent logEvent) =>
        new RedactSensitiveFieldsEnricher().Enrich(logEvent, new SimplePropertyFactory());

    private static LogEvent CreateEvent(params LogEventProperty[] properties)
    {
        var logEvent = new LogEvent(
            DateTimeOffset.UtcNow,
            LogEventLevel.Information,
            exception: null,
            new MessageTemplate("test", []),
            []);

        foreach (var property in properties)
        {
            logEvent.AddOrUpdateProperty(property);
        }

        return logEvent;
    }

    private static string? Scalar(LogEvent logEvent, string name) =>
        ((ScalarValue)logEvent.Properties[name]).Value?.ToString();

    private static string? ScalarOf(StructureValue structure, string name) =>
        ((ScalarValue)structure.Properties.Single(p => p.Name == name).Value).Value?.ToString();

    private sealed class SimplePropertyFactory : ILogEventPropertyFactory
    {
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, value as LogEventPropertyValue ?? new ScalarValue(value));
    }
}
