using FluentAssertions;
using LearnStack.Infrastructure.Observability.Serilog;
using LearnStack.SharedKernel.Secrets;
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
    private const string Redacted = SensitiveTokenCatalog.RedactedValue;

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

    [Fact]
    public void Redacts_Dictionary_Keys_And_Preserves_Ordinary_And_Non_String_Keys()
    {
        var ordinary = new ScalarValue("public");
        var numeric = new ScalarValue("numeric-key-value");
        var dictionary = new DictionaryValue(new Dictionary<ScalarValue, LogEventPropertyValue>
        {
            [new ScalarValue("AuthenticationHeader")] = new ScalarValue("private-header-canary"),
            [new ScalarValue("ClassName")] = ordinary,
            [new ScalarValue(7)] = numeric,
        });
        var logEvent = CreateEvent(new LogEventProperty("Context", dictionary));

        Enrich(logEvent);

        var redacted = (DictionaryValue)logEvent.Properties["Context"];
        redacted.Elements.Keys.Should().BeEquivalentTo(dictionary.Elements.Keys);
        ((ScalarValue)redacted.Elements[new ScalarValue("AuthenticationHeader")]).Value.Should().Be(Redacted);
        redacted.Elements[new ScalarValue("ClassName")].Should().BeSameAs(ordinary);
        redacted.Elements[new ScalarValue(7)].Should().BeSameAs(numeric);
        ((ScalarValue)dictionary.Elements[new ScalarValue("AuthenticationHeader")]).Value
            .Should().Be("private-header-canary", "redaction reconstructs the value without mutating its input");
    }

    [Fact]
    public void Redacts_Sequence_Descendants_Without_Dropping_Clean_Items_Before_Or_After_Them()
    {
        var first = new ScalarValue("first");
        var last = new ScalarValue("last");
        var account = new StructureValue(
        [
            new LogEventProperty("Password", new ScalarValue("private-sequence-canary")),
            new LogEventProperty("Name", new ScalarValue("public")),
        ], "Account");
        var sequence = new SequenceValue([first, account, last]);
        var logEvent = CreateEvent(new LogEventProperty("Accounts", sequence));

        Enrich(logEvent);

        var redacted = (SequenceValue)logEvent.Properties["Accounts"];
        redacted.Elements.Should().HaveCount(3);
        redacted.Elements[0].Should().BeSameAs(first);
        redacted.Elements[2].Should().BeSameAs(last);
        var redactedAccount = (StructureValue)redacted.Elements[1];
        redactedAccount.TypeTag.Should().Be("Account");
        ScalarOf(redactedAccount, "Password").Should().Be(Redacted);
        ScalarOf(redactedAccount, "Name").Should().Be("public");
        ScalarOf(account, "Password").Should().Be("private-sequence-canary");
    }

    [Fact]
    public void Redacts_Deeply_Mixed_Structures_Dictionaries_And_Sequences()
    {
        var leaf = new StructureValue(
            [new LogEventProperty("ApiKey", new ScalarValue("private-deep-canary"))]);
        var dictionary = new DictionaryValue(new Dictionary<ScalarValue, LogEventPropertyValue>
        {
            [new ScalarValue("Items")] = new SequenceValue([leaf]),
        });
        var root = new StructureValue(
            [new LogEventProperty("Groups", new SequenceValue([dictionary]))], "Root");
        var logEvent = CreateEvent(new LogEventProperty("Context", root));

        Enrich(logEvent);

        var redactedRoot = (StructureValue)logEvent.Properties["Context"];
        var groups = (SequenceValue)redactedRoot.Properties.Single().Value;
        var redactedDictionary = (DictionaryValue)groups.Elements.Single();
        var items = (SequenceValue)redactedDictionary.Elements[new ScalarValue("Items")];
        var redactedLeaf = (StructureValue)items.Elements.Single();
        redactedRoot.TypeTag.Should().Be("Root");
        ScalarOf(redactedLeaf, "ApiKey").Should().Be(Redacted);
        redactedRoot.ToString().Should().NotContain("private-deep-canary");
    }

    [Fact]
    public void Leaves_Clean_Composite_Values_Unchanged_By_Reference()
    {
        var leaf = new StructureValue([new LogEventProperty("Name", new ScalarValue("public"))]);
        var dictionary = new DictionaryValue(new Dictionary<ScalarValue, LogEventPropertyValue>
        {
            [new ScalarValue("ClassName")] = leaf,
        });
        var sequence = new SequenceValue([dictionary]);
        var logEvent = CreateEvent(
            new LogEventProperty("Details", leaf),
            new LogEventProperty("Groups", dictionary),
            new LogEventProperty("Items", sequence));

        Enrich(logEvent);

        logEvent.Properties["Details"].Should().BeSameAs(leaf);
        logEvent.Properties["Groups"].Should().BeSameAs(dictionary);
        logEvent.Properties["Items"].Should().BeSameAs(sequence);
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
