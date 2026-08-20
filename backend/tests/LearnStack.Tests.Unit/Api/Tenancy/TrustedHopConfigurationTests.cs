using FluentAssertions;
using LearnStack.Api.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Api.Tenancy;

/// <summary>
/// The composition-root refusal of a half-configured trusted hop, per
/// <see href="../../../../../docs/decisions/0036-tenant-resolution-trusted-inputs.md">ADR-0036
/// § Effective host and the trusted hop</see>.
/// </summary>
/// <remarks>
/// Both lists or neither. The hop is an <b>AND</b> of network position and a
/// secret, so exactly one of them configured is not a weaker hop — it is a hop
/// that silently is not one, and the only symptom is an anonymous page render
/// answering 404. `TrustedHopOptions.Validate()` checks the shape of each entry
/// and nothing about the pair, so nothing caught this.
/// </remarks>
public sealed class TrustedHopConfigurationTests
{
    private const string Secret = "a-secret-long-enough-to-be-a-secret-32";

    [Fact]
    public void Networks_With_No_Secrets_Is_Refused()
    {
        // Network position alone: on a container bridge or a pod CIDR
        // everything in the mesh is the gateway's neighbour.
        var act = () => Build(networks: "10.0.0.0/8", secrets: null);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Networks with no Secrets*");
    }

    [Fact]
    public void Secrets_With_No_Networks_Is_Refused_Too()
    {
        // The mirror image, and it was caught by nothing at all before: a
        // secret alone is defeated by one leak into a bundle or a log.
        var act = () => Build(networks: null, secrets: Secret);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Secrets with no Networks*");
    }

    [Fact]
    public void Neither_Is_Fine()
    {
        // A deployment with nothing in front of the API legitimately has no
        // hop. That is both lists empty, and it is not an error.
        var act = () => Build(networks: null, secrets: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Both_Is_Fine()
    {
        var act = () => Build(networks: "10.0.0.0/8", secrets: Secret);

        act.Should().NotThrow();
    }

    [Fact]
    public void A_Short_Secret_Is_Still_Refused_On_Its_Own_Terms()
    {
        // The pair check must not have replaced the per-entry one.
        var act = () => Build(networks: "10.0.0.0/8", secrets: "too-short");

        act.Should().Throw<InvalidOperationException>().WithMessage("*shorter than*");
    }

    private static void Build(string? networks, string? secrets)
    {
        var settings = new Dictionary<string, string?>();
        if (networks is not null)
        {
            settings[$"{TrustedHopOptions.SectionName}:Networks:0"] = networks;
        }

        if (secrets is not null)
        {
            settings[$"{TrustedHopOptions.SectionName}:Secrets:0"] = secrets;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        new ServiceCollection().AddLearnStackTenancyEdge(configuration);
    }
}
