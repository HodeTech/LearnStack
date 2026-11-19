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
        RebindToolInput.TryRead(name => environment.GetValueOrDefault(name), () => line,
            new ConfigurationBuilder().Build(), out input);

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
    [InlineData("ConnectionStrings__IdentityRebind")]
    [InlineData("Identity__AuthorityId")]
    [InlineData("Authentication__Tenant__Issuer")]
    [InlineData("Deployment__Mode")]
    [InlineData("LEARNSTACK_OPERATOR_TOKEN")]
    [InlineData("LEARNSTACK_REBIND_REALM_CLIENT_ID")]
    [InlineData("LEARNSTACK_REBIND_REALM_CLIENT_SECRET")]
    public void Each_Required_Value_Missing_Refuses(string missing)
    {
        var environment = Valid();
        environment.Remove(missing);

        Read(environment, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    [InlineData("Development,SaaS")]
    [InlineData("development")]
    [InlineData("saas")]
    [InlineData("Production")]
    [InlineData(" Development")]
    public void The_Deployment_Mode_Is_A_Declared_Name_Exactly(string mode)
    {
        var environment = Valid();
        environment["Deployment__Mode"] = mode;
        environment["Authentication__Tenant__Issuer"] = "https://new.example.test/realms/learnstack";

        Read(environment, out _).Should().BeFalse("the API refuses ordinals and lists, so the tool must not guess a mode");
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
