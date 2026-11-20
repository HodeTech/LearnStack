using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.Tools.IdentityAuthorityRebind;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace LearnStack.Tests.Integration;

/// <summary>
/// What the tool accepts from its environment and one line of standard input (ADR-0104 § 3), and what it refuses
/// without saying why: a refusal names no value, so a mistake cannot echo a secret.
/// </summary>
public sealed class RebindToolInputTests
{
    private const string Authority = "019a1929-5450-7bbb-8df1-516e8e002501";

    private static Dictionary<string, string?> Valid() => new()
    {
        ["ConnectionStrings__IdentityRebind"] = "Host=db.example;Database=learnstack;Username=learnstack_identity_rebind;Password=x", // leakwatch:ignore:database-connection-string
        ["Identity__AuthorityId"] = Authority,
        ["Authentication__Tenant__Issuer"] = "https://new.example.test/realms/learnstack",
        ["Deployment__Mode"] = "SaaS",
        ["LEARNSTACK_OPERATOR_TOKEN"] = "  header.payload.signature  ",
        ["LEARNSTACK_REBIND_REALM_CLIENT_ID"] = "reader",
        ["LEARNSTACK_REBIND_REALM_CLIENT_SECRET"] = "secret-value",
    };

    private static bool Read(Dictionary<string, string?> environment, out RebindToolInput? input, string? line = null) =>
        Read(environment, out input, out _, line);

    private static bool Read(Dictionary<string, string?> environment, out RebindToolInput? input, out string field, string? line = null) =>
        RebindToolInput.TryRead(name => environment.GetValueOrDefault(name), () => line,
            new ConfigurationBuilder().Build(), out input, out field);

    [Fact]
    public void A_Complete_Environment_Is_Read_And_The_Token_Is_Trimmed()
    {
        Read(Valid(), out var input).Should().BeTrue();

        input!.OperatorToken.Should().Be("header.payload.signature");
        input.Target.AuthorityId.Value.Should().Be(Guid.Parse(Authority));
        input.Target.Issuer.Should().Be("https://new.example.test/realms/learnstack");
        input.Ticket.Should().BeNull();
        input.Mode.Should().Be(LearnStack.SharedKernel.Hosting.DeploymentMode.SaaS);
    }

    [Theory]
    [InlineData("ConnectionStrings__IdentityRebind", "connection_string")]
    [InlineData("Identity__AuthorityId", "authority_id")]
    [InlineData("Authentication__Tenant__Issuer", "issuer")]
    [InlineData("Deployment__Mode", "deployment_mode")]
    [InlineData("LEARNSTACK_OPERATOR_TOKEN", "operator_token")]
    [InlineData("LEARNSTACK_REBIND_REALM_CLIENT_ID", "realm_client")]
    [InlineData("LEARNSTACK_REBIND_REALM_CLIENT_SECRET", "realm_client")]
    public void Each_Required_Value_Missing_Refuses_Naming_Only_The_Setting(string missing, string field)
    {
        var environment = Valid();
        environment.Remove(missing);

        Read(environment, out _, out var named).Should().BeFalse();
        named.Should().Be(field);
        RebindToolInput.Fields.All.Should().Contain(named, "the name is one of the closed set the runbook lists");
    }

    [Fact]
    public void The_Closed_Field_Names_Are_Distinct_Lowercase_Tokens()
    {
        RebindToolInput.Fields.All.Should().OnlyHaveUniqueItems()
            .And.OnlyContain(name => name.All(character => char.IsAsciiLetterLower(character) || character == '_'));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Development,SaaS")]
    [InlineData("Production")]
    [InlineData("")]
    public void The_Deployment_Mode_Is_A_Declared_Name_And_Never_An_Ordinal_Or_A_List(string mode)
    {
        var environment = Valid();
        environment["Deployment__Mode"] = mode;

        Read(environment, out _, out var field).Should().BeFalse("the API refuses ordinals and lists, so the tool must not guess a mode");
        field.Should().Be("deployment_mode");
    }

    [Theory]
    [InlineData("development", LearnStack.SharedKernel.Hosting.DeploymentMode.Development)]
    [InlineData("saas", LearnStack.SharedKernel.Hosting.DeploymentMode.SaaS)]
    [InlineData(" SaaS ", LearnStack.SharedKernel.Hosting.DeploymentMode.SaaS)]
    [InlineData("SELFHOSTEDAIRGAPPED", LearnStack.SharedKernel.Hosting.DeploymentMode.SelfHostedAirGapped)]
    public void The_Deployment_Mode_Is_Read_The_Way_The_Api_Reads_It_Without_Regard_To_Case_Or_Padding(
        string mode, LearnStack.SharedKernel.Hosting.DeploymentMode expected)
    {
        var environment = Valid();
        environment["Deployment__Mode"] = mode;
        environment["Authentication__Tenant__Issuer"] = "https://new.example.test/realms/learnstack";

        Read(environment, out var input).Should().BeTrue("an API started with this spelling accepts it, so the tool must too");
        input!.Mode.Should().Be(expected);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("019a192954507bbb8df1516e8e002501")]
    public void The_Authority_Must_Be_A_Nonempty_Hyphenated_Guid(string authority)
    {
        var environment = Valid();
        environment["Identity__AuthorityId"] = authority;

        Read(environment, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("learnstack_app")]
    [InlineData("learnstack_migration")]
    [InlineData("learnstack_platform")]
    public void Only_The_Rebind_Role_Is_Accepted_In_The_Connection_String(string role)
    {
        var environment = Valid();
        environment["ConnectionStrings__IdentityRebind"] = $"Host=db.example;Database=learnstack;Username={role};Password=x"; // leakwatch:ignore:database-connection-string

        Read(environment, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("http://localhost:8080/realms/learnstack", "SaaS", false)]
    [InlineData("http://localhost:8080/realms/learnstack", "SelfHostedOnline", false)]
    [InlineData("http://localhost:8080/realms/learnstack", "Development", true)]
    [InlineData("http://new.example.test/realms/learnstack", "Development", false)]
    [InlineData("https://new.example.test/realms/learnstack", "Dedicated", true)]
    [InlineData("https://new.example.test/realms/other", "Dedicated", false)]
    [InlineData("https://new.example.test/realms/learnstack?x=1", "Dedicated", false)]
    public void The_Target_Issuer_Follows_The_Grammar_And_Loopback_Http_Is_Development_Only(string issuer, string mode, bool accepted)
    {
        var environment = Valid();
        environment["Authentication__Tenant__Issuer"] = issuer;
        environment["Deployment__Mode"] = mode;

        Read(environment, out _).Should().Be(accepted);
    }

    [Fact]
    public void The_Printed_Line_Names_The_Detail_After_The_Correlation_And_Before_The_Counts()
    {
        var line = RebindTool.Line(
            new IdentityRebindOutcome(IdentityRebindResult.Refused, IdentityRebindRefusal.RoleInvalid, null, 2, IdentityRebindDetail.RoleGrants),
            "rebind-abc");

        line.Should().Be("rebind: outcome=refused_role_invalid correlation=rebind-abc detail=role_grants sessions=2");
        RebindTool.Line(new IdentityRebindOutcome(IdentityRebindResult.AlreadyApplied), "rebind-abc")
            .Should().Be("rebind: outcome=already_applied correlation=rebind-abc", "a class with one cause carries no detail");
    }

    [Fact]
    public void A_Refusal_Before_The_Database_Is_Printed_And_Counted_Under_The_Same_Label()
    {
        var seen = new System.Collections.Concurrent.ConcurrentBag<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, subscriber) =>
        {
            if (instrument.Meter.Name == IdentityRebindMetrics.MeterName) subscriber.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "outcome") seen.Add((string)tag.Value!);
            }
        });
        listener.Start();
        var output = new StringWriter();

        RebindTool.RefuseInput(output, RebindToolInput.Fields.ConnectionString).Should().Be(RebindTool.Refused);
        RebindTool.Refuse(output, "operator_missing_permission", "rebind-abc").Should().Be(RebindTool.Refused);

        output.ToString().Replace("\r", string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries).Should().Equal(
            "rebind: outcome=refused_input detail=connection_string",
            "rebind: outcome=refused_operator_missing_permission correlation=rebind-abc");
        // The meter is process-wide and other cases record on it too, so the labels are looked for, not counted.
        seen.Should().Contain(["refused_input", "refused_operator_missing_permission"]);
    }

    [Fact]
    public void The_Token_Falls_Back_To_One_Line_Of_Standard_Input_And_Never_To_An_Argument()
    {
        var environment = Valid();
        environment.Remove("LEARNSTACK_OPERATOR_TOKEN");

        Read(environment, out var input, line: " piped.token.value ").Should().BeTrue();
        input!.OperatorToken.Should().Be("piped.token.value");
        Read(environment, out _, line: "   ").Should().BeFalse();
        Read(environment, out _, line: null).Should().BeFalse();
    }

    [Fact]
    public void A_Token_Over_The_Length_Bound_Refuses_And_A_Blank_Ticket_Is_No_Ticket()
    {
        var environment = Valid();
        environment["LEARNSTACK_OPERATOR_TOKEN"] = new string('a', 16 * 1024 + 1);
        Read(environment, out _).Should().BeFalse();

        environment["LEARNSTACK_OPERATOR_TOKEN"] = "a.b.c";
        environment["LEARNSTACK_REBIND_TICKET"] = "   ";
        Read(environment, out var input).Should().BeTrue();
        input!.Ticket.Should().BeNull();

        environment["LEARNSTACK_REBIND_TICKET"] = "CHG-104";
        Read(environment, out input).Should().BeTrue();
        input!.Ticket.Should().Be("CHG-104");
    }
}
