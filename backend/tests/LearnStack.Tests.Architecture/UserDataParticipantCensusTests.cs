using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0107 § 1: every module whose model holds a person reference registers exactly one
/// <see cref="IUserDataParticipant"/> through the composition root, from inside its own boundary, and no module
/// references another module's participant. Each detector is proved able to fail by a planted violation.
/// </summary>
public sealed class UserDataParticipantCensusTests
{
    [Fact]
    public void Every_Module_With_A_Person_Reference_Registers_One_User_Data_Participant()
    {
        var registered = RegisteredParticipants();
        var referencing = ModulesWithPersonReferences();

        referencing.Should().BeEquivalentTo(["Audit", "Customization", "Education", "Identity", "Tenancy"],
            "the census covers every module whose model maps a person reference");
        CensusOffenders(referencing, registered).Should().BeEmpty();

        // Planted: a module that stops registering its participant is named.
        foreach (var module in referencing)
        {
            var missing = registered.Where(type => ModuleOf(type.Assembly) != module).ToArray();
            CensusOffenders(referencing, missing).Should().ContainSingle()
                .Which.Should().Be("Missing user data participant registration: " + module);
        }

        // Planted: a second participant inside one module, and one outside every module, are named.
        CensusOffenders(referencing, [.. registered, typeof(PlantedParticipant)])
            .Should().Contain("User data participant outside a module boundary: " + typeof(PlantedParticipant).FullName);
        var twice = registered.Append(registered.First(type => ModuleOf(type.Assembly) == "Identity")).ToArray();
        CensusOffenders(referencing, twice).Should().Contain("More than one user data participant: Identity");
    }

    [Fact]
    public void No_Module_References_Another_Modules_User_Data_Participant()
    {
        var registered = RegisteredParticipants();
        var references = ModuleAssemblies().ToDictionary(assembly => assembly.GetName().Name!,
            assembly => (IReadOnlyCollection<string>)assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray(),
            StringComparer.Ordinal);

        ReferenceOffenders(registered, references).Should().BeEmpty();

        // Planted: Education reaching Tenancy's participant assembly is named.
        var tenancy = registered.Single(type => ModuleOf(type.Assembly) == "Tenancy").Assembly.GetName().Name!;
        var planted = new Dictionary<string, IReadOnlyCollection<string>>(references, StringComparer.Ordinal)
        {
            ["LearnStack.Modules.Education.Infrastructure"] = [.. references["LearnStack.Modules.Education.Infrastructure"], tenancy],
        };
        ReferenceOffenders(registered, planted).Should().ContainSingle()
            .Which.Should().Be("LearnStack.Modules.Education.Infrastructure references " + tenancy);
    }

    /// <summary>Every participant the API composition actually registers, Identity's and the root's own.</summary>
    private static Type[] RegisteredParticipants()
    {
        var services = new ServiceCollection();
        services.AddIdentityContextualFoundation();
        services.AddDataExportCustody(new ConfigurationBuilder().Build());
        return services.Where(descriptor => descriptor.ServiceType == typeof(IUserDataParticipant))
            .Select(descriptor => descriptor.ImplementationType
                ?? throw new InvalidOperationException("A participant is registered by its concrete type."))
            .ToArray();
    }

    /// <summary>A module whose mapped model has a <see cref="UserId"/> property holds a person reference.</summary>
    private static string[] ModulesWithPersonReferences() =>
        Modules.Mapped.Where(module =>
            {
                using var context = module.Context();
                return context.Model.GetEntityTypes().SelectMany(entity => entity.GetProperties())
                    .Any(property => property.ClrType == typeof(UserId) || property.ClrType == typeof(UserId?));
            })
            .Select(module => ModuleOf(module.Domain)!)
            .Order(StringComparer.Ordinal).ToArray();

    private static List<string> CensusOffenders(IReadOnlyCollection<string> referencing, IReadOnlyCollection<Type> registered)
    {
        var offenders = new List<string>();
        foreach (var participant in registered.Where(type => ModuleOf(type.Assembly) is null))
        {
            offenders.Add("User data participant outside a module boundary: " + participant.FullName);
        }

        foreach (var group in registered.Where(type => ModuleOf(type.Assembly) is not null).GroupBy(type => ModuleOf(type.Assembly)!))
        {
            if (group.Count() > 1)
            {
                offenders.Add("More than one user data participant: " + group.Key);
            }
        }

        foreach (var module in referencing)
        {
            if (!registered.Any(type => ModuleOf(type.Assembly) == module))
            {
                offenders.Add("Missing user data participant registration: " + module);
            }
        }

        return offenders;
    }

    private static List<string> ReferenceOffenders(IReadOnlyCollection<Type> participants,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>> references)
    {
        var offenders = new List<string>();
        foreach (var participant in participants)
        {
            var owner = ModuleOf(participant.Assembly);
            var home = participant.Assembly.GetName().Name!;
            foreach (var (assembly, referenced) in references.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (ModuleOf(assembly) is { } module && module != owner && referenced.Contains(home, StringComparer.Ordinal))
                {
                    offenders.Add(assembly + " references " + home);
                }
            }
        }

        return offenders;
    }

    private static readonly string[] Legs = ["Domain", "Application.Contracts", "Application", "Infrastructure"];

    private static IEnumerable<Assembly> ModuleAssemblies() =>
        Modules.Names.SelectMany(module => Legs.Select(leg => Assembly.Load("LearnStack.Modules." + module + "." + leg)));

    private static string? ModuleOf(Assembly assembly) => ModuleOf(assembly.GetName().Name!);

    private static string? ModuleOf(string assembly)
    {
        const string prefix = "LearnStack.Modules.";
        if (!assembly.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var rest = assembly[prefix.Length..];
        return rest[..rest.IndexOf('.', StringComparison.Ordinal)];
    }

    private sealed class PlantedParticipant : IUserDataParticipant
    {
        public string Key => "planted";

        public Task ExportAsync(UserDataSubject subject, IUserDataExportWriter writer, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyCollection<RetainedReferenceCount>> DescribeRetainedAsync(UserDataSubject subject,
            CancellationToken cancellationToken) => Task.FromResult<IReadOnlyCollection<RetainedReferenceCount>>([]);
    }
}
