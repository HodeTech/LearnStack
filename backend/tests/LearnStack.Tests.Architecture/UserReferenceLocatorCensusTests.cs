using System.Reflection;
using FluentAssertions;
using LearnStack.Api.Composition;
using LearnStack.Modules.Identity.Infrastructure.Bindings;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IdentityDomain = LearnStack.Modules.Identity.Domain;
using TenancyDomain = LearnStack.Modules.Tenancy.Domain;

namespace LearnStack.Tests.Architecture;

/// <summary>
/// ADR-0107 § 1 and ADR-0108 § 7: every person reference an audited entity carries into its audit snapshot is declared,
/// redactable or retained, by its own module's registered <see cref="IUserReferenceLocator"/>, and no module references
/// another module's locator. Each detector is proved able to fail by a planted violation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Derived from the models, not listed.</b> The audited entities are the declared types of the merged audit
/// catalogue; their person references are read from each module's mapped model by CLR type (<see cref="UserId"/>, the
/// Identity <c>MembershipId</c>) and, for a reference stored as a plain <see cref="Guid"/> the types cannot see (the
/// consent's <c>[PiiSensitive]</c> Membership), by name: a <see cref="Guid"/> property whose name ends in <c>UserId</c>
/// or <c>MembershipId</c>. A contained collection is walked by the capture's own containment rule (a non-root entity
/// exactly one relationship reaches through its principal's navigation), with the foreign key to its owner left out as
/// the capture leaves it out, so its members' references are the paths <c>Navigation/*/Property</c>.
/// </para>
/// <para>
/// A reference the grammar cannot express (inside a contained single navigation, or one level deeper) fails the census
/// until the grammar grows. An array of identifiers held in a <c>jsonb</c> property is invisible to a type census, so an
/// <c>Elements</c> declaration is instead checked to name a mapped <c>jsonb</c> property.
/// </para>
/// </remarks>
public sealed class UserReferenceLocatorCensusTests
{
    [Fact]
    public void Every_PII_Module_RegistersUserReferenceLocator()
    {
        var locators = RegisteredLocators();
        var catalog = new UserReferenceCatalog(locators.Select(locator => locator.Instance));
        var models = ProductionModels();
        var references = models.SelectMany(model => References(model, [])).ToList();

        references.Select(reference => reference.Module).Distinct().Should().BeEquivalentTo(ReferencingModules,
            "every module whose audited entities hold a person reference is in the census");
        references.Should().Contain(new PersonReference("Tenancy", typeof(TenancyDomain.Tenant),
            "FeatureFlags/*/UpdatedBy", UserReferenceKind.User, IsKey: false), "the census walks contained collections");
        references.Should().Contain(new PersonReference("Tenancy", typeof(TenancyDomain.MembershipFieldConsent),
            "MembershipId", UserReferenceKind.Membership, IsKey: false), "the census reads a plain Guid reference by its name");
        references.Should().Contain(new PersonReference("Identity", typeof(IdentityDomain.Membership), "Id",
            UserReferenceKind.Membership, IsKey: true), "the census sees an entity whose own key is a person reference");
        Offenders(models, locators, catalog).Should().BeEmpty();

        // Planted: a module that stops registering its locator is named, with each of its references.
        foreach (var module in references.Select(reference => reference.Module).Distinct())
        {
            var missing = locators.Where(locator => locator.Module != module).ToList();
            Offenders(models, missing, new UserReferenceCatalog(missing.Select(locator => locator.Instance)))
                .Should().Contain("Missing user reference locator: " + module)
                .And.Contain(offender => offender.StartsWith("Undeclared person reference: ", StringComparison.Ordinal));
        }

        // Planted: a synthetic module whose audited entity holds a typed, an untyped and a contained person reference and
        // registers no locator; then one that declares them with a wrong kind, a stale path and another module's entity.
        var planted = new ModuleModel("Planted", PlantedModel(), [typeof(PlantedAggregate)]);
        Offenders([.. models, planted], locators, catalog).Should()
            .Contain("Missing user reference locator: Planted")
            .And.Contain("Undeclared person reference: PlantedAggregate.Owner")
            .And.Contain("Undeclared person reference: PlantedAggregate.DelegateMembershipId")
            .And.Contain("Undeclared person reference: PlantedAggregate.Members/*/Reviewer")
            .And.Contain("Person reference outside the path grammar: PlantedAggregate.Lead/Approver");
        var wrong = new RegisteredLocator("Planted", new PlantedLocator());
        Offenders([.. models, planted], [.. locators, wrong],
                new UserReferenceCatalog([.. locators.Select(locator => locator.Instance), wrong.Instance])).Should()
            .Contain("Wrong kind for PlantedAggregate.Owner: Membership, the model holds User")
            .And.Contain("Stale declaration: PlantedAggregate.Retired")
            .And.Contain("Locator of Planted declares another module's entity: Membership")
            .And.NotContain("Missing user reference locator: Planted");
        Offenders(models, [.. locators, locators[0] with { }], catalog).Should()
            .Contain("More than one user reference locator: " + locators[0].Module);
    }

    [Fact]
    public void No_Module_References_Another_Modules_User_Reference_Locator()
    {
        var locators = RegisteredLocators().Select(locator => locator.Instance.GetType()).ToArray();
        var references = Modules.Names
            .SelectMany(module => Legs.Select(leg => Assembly.Load("LearnStack.Modules." + module + "." + leg)))
            .ToDictionary(assembly => assembly.GetName().Name!,
                assembly => (IReadOnlyCollection<string>)assembly.GetReferencedAssemblies().Select(name => name.Name!).ToArray(),
                StringComparer.Ordinal);

        UserDataParticipantCensusTests.ReferenceOffenders(locators, references).Should().BeEmpty();

        // Planted: Education reaching Tenancy's locator assembly is named.
        var tenancy = locators.Single(type => ModuleOf(type.Assembly) == "Tenancy").Assembly.GetName().Name!;
        var planted = new Dictionary<string, IReadOnlyCollection<string>>(references, StringComparer.Ordinal)
        {
            ["LearnStack.Modules.Education.Infrastructure"] = [.. references["LearnStack.Modules.Education.Infrastructure"], tenancy],
        };
        UserDataParticipantCensusTests.ReferenceOffenders(locators, planted).Should().ContainSingle()
            .Which.Should().Be("LearnStack.Modules.Education.Infrastructure references " + tenancy);
    }

    private static readonly string[] Legs = ["Domain", "Application.Contracts", "Application", "Infrastructure"];

    private static readonly string[] ReferencingModules = ["Customization", "Education", "Identity", "Tenancy"];

    /// <summary>Every locator the API composition registers, Identity's and the root's own, with its module.</summary>
    private static List<RegisteredLocator> RegisteredLocators()
    {
        var services = new ServiceCollection();
        services.AddIdentityContextualFoundation();
        services.AddDataExportCustody(new ConfigurationBuilder().Build());
        return
        [
            .. services.Where(descriptor => descriptor.ServiceType == typeof(IUserReferenceLocator))
                .Select(descriptor => descriptor.ImplementationType
                    ?? throw new InvalidOperationException("A locator is registered by its concrete type."))
                .Select(type => new RegisteredLocator(ModuleOf(type.Assembly), (IUserReferenceLocator)Activator.CreateInstance(type)!)),
        ];
    }

    /// <summary>Each mapped module's model with the entity types the merged audit catalogue declares from its domain.</summary>
    private static List<ModuleModel> ProductionModels()
    {
        var audited = AuditCatalogDiscovery.Catalogue().All
            .Select(entry => entry.EntityType).OfType<Type>().Distinct().ToList();
        return
        [
            .. Modules.Mapped.Select(module =>
            {
                using var context = module.Context();
                return new ModuleModel(ModuleOf(module.Domain)!, context.GetService<IDesignTimeModel>().Model,
                    [.. audited.Where(type => type.Assembly == module.Domain)]);
            }),
        ];
    }

    private static List<string> Offenders(IReadOnlyCollection<ModuleModel> models, IReadOnlyCollection<RegisteredLocator> locators,
        UserReferenceCatalog catalog)
    {
        var offenders = new List<string>();
        var found = models.SelectMany(model => References(model, offenders)).ToList();
        foreach (var locator in locators)
        {
            if (locator.Module is null)
            {
                offenders.Add("User reference locator outside a module boundary: " + locator.Instance.GetType().FullName);
            }

            foreach (var entity in locator.Instance.Redactable.Select(declaration => declaration.EntityType)
                .Concat(locator.Instance.Retained.Select(reference => reference.EntityType)).Distinct()
                .Where(entity => ModuleOf(entity.Assembly) != locator.Module))
            {
                offenders.Add("Locator of " + locator.Module + " declares another module's entity: " + entity.Name);
            }
        }

        foreach (var module in locators.Where(locator => locator.Module is not null).GroupBy(locator => locator.Module!)
            .Where(group => group.Count() > 1))
        {
            offenders.Add("More than one user reference locator: " + module.Key);
        }

        foreach (var reference in found)
        {
            var declared = catalog.Redactable.FirstOrDefault(declaration =>
                declaration.EntityType == reference.Entity && declaration.Path.ToString() == reference.Path);
            var retained = catalog.Retained.FirstOrDefault(kept =>
                kept.EntityType == reference.Entity && kept.Path.ToString() == reference.Path);
            if (declared is not null)
            {
                if (declared.Kind != reference.Kind)
                {
                    offenders.Add($"Wrong kind for {reference.Entity.Name}.{reference.Path}: {declared.Kind}, the model holds {reference.Kind}");
                }
            }
            else if (retained is not null)
            {
                if (!reference.IsKey)
                {
                    offenders.Add($"Retained reference is not the entity's own key: {reference.Entity.Name}.{reference.Path}");
                }
            }
            else
            {
                offenders.Add($"Undeclared person reference: {reference.Entity.Name}.{reference.Path}");
            }
        }

        var byModule = models.ToDictionary(model => model.Module, StringComparer.Ordinal);
        foreach (var declaration in catalog.Redactable.Where(declaration =>
            !found.Any(reference => reference.Entity == declaration.EntityType && reference.Path == declaration.Path.ToString())
            && !IsJsonArrayDeclaration(declaration, byModule)))
        {
            offenders.Add($"Stale declaration: {declaration.EntityName}.{declaration.Path}");
        }

        foreach (var kept in catalog.Retained.Where(kept => !found.Any(reference =>
            reference.Entity == kept.EntityType && reference.Path == kept.Path.ToString() && reference.IsKey)))
        {
            offenders.Add($"Stale retained reference: {kept.EntityName}.{kept.Path}");
        }

        foreach (var module in found.Select(reference => reference.Module).Distinct()
            .Where(module => !locators.Any(locator => locator.Module == module)))
        {
            offenders.Add("Missing user reference locator: " + module);
        }

        return offenders;
    }

    /// <summary>
    /// The person references of a module's audited entities as the capture writes them: each entity's own properties,
    /// and its contained collections' members' properties without the foreign key to the owner.
    /// </summary>
    private static IEnumerable<PersonReference> References(ModuleModel model, List<string> offenders)
    {
        foreach (var type in model.Audited)
        {
            // An audited type the module does not map is captured from no tracker, so its rows carry no snapshot.
            if (model.Model.FindEntityType(type) is not { } entity)
            {
                continue;
            }

            foreach (var property in entity.GetProperties())
            {
                if (KindOf(property) is { } kind)
                {
                    yield return new PersonReference(model.Module, type, property.Name, kind, property.IsPrimaryKey());
                }
            }

            foreach (var navigation in Containments(entity))
            {
                foreach (var reference in Contained(model.Module, type, navigation, navigation.Name, depth: 1, offenders))
                {
                    yield return reference;
                }
            }
        }
    }

    private static IEnumerable<PersonReference> Contained(string module, Type owner, IReadOnlyNavigation navigation, string path,
        int depth, List<string> offenders)
    {
        var member = navigation.TargetEntityType;
        foreach (var property in member.GetProperties().Where(property => !navigation.ForeignKey.Properties.Contains(property)))
        {
            if (KindOf(property) is not { } kind)
            {
                continue;
            }

            if (depth == 1 && navigation.IsCollection)
            {
                yield return new PersonReference(module, owner, UserReferencePath.Member(navigation.Name, property.Name).ToString(),
                    kind, IsKey: false);
            }
            else
            {
                offenders.Add($"Person reference outside the path grammar: {owner.Name}.{path}/{property.Name}");
            }
        }

        // Bounded as the capture bounds containment; a well-formed model ends at a root long before.
        if (depth < 8)
        {
            foreach (var inner in Containments(member))
            {
                foreach (var reference in Contained(module, owner, inner, path + "/" + inner.Name, depth + 1, offenders))
                {
                    yield return reference;
                }
            }
        }
    }

    /// <summary>The capture's containment rule (ADR-0044 Amendment 6 § 4), read from the model.</summary>
    private static IEnumerable<IReadOnlyNavigation> Containments(IReadOnlyEntityType entity) =>
        entity.GetNavigations().Where(navigation => !navigation.IsOnDependent && IsContained(navigation));

    private static bool IsContained(IReadOnlyNavigation navigation)
    {
        var target = navigation.TargetEntityType;
        if (target.ClrType.GetInterfaces().Any(contract =>
            contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IAggregateRoot<>)))
        {
            return false;
        }

        var reaching = target.GetForeignKeys().Where(foreignKey =>
            foreignKey.PrincipalToDependent is not null && foreignKey.PrincipalEntityType.ClrType != target.ClrType).ToList();
        return reaching.Count == 1 && reaching[0] == navigation.ForeignKey;
    }

    /// <summary>What a mapped property holds: a person by its <see cref="UserId"/> type, a Membership by its identifier type,
    /// or either as a plain <see cref="Guid"/> named for it.</summary>
    private static UserReferenceKind? KindOf(IReadOnlyProperty property)
    {
        var type = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
        if (type == typeof(UserId))
        {
            return UserReferenceKind.User;
        }

        if (type == typeof(IdentityDomain.MembershipId))
        {
            return UserReferenceKind.Membership;
        }

        if (type == typeof(Guid))
        {
            if (property.Name.EndsWith("UserId", StringComparison.Ordinal))
            {
                return UserReferenceKind.User;
            }

            if (property.Name.EndsWith("MembershipId", StringComparison.Ordinal))
            {
                return UserReferenceKind.Membership;
            }
        }

        return null;
    }

    /// <summary>Whether an <c>Elements</c> declaration names a mapped <c>jsonb</c> string property of its entity.</summary>
    private static bool IsJsonArrayDeclaration(UserReferenceDeclaration declaration, IReadOnlyDictionary<string, ModuleModel> models) =>
        declaration.Path.Shape == UserReferenceShape.Elements
        && ModuleOf(declaration.EntityType.Assembly) is { } module
        && models.TryGetValue(module, out var model)
        && model.Model.FindEntityType(declaration.EntityType)?.FindProperty(declaration.Path.Property) is { } property
        && property.ClrType == typeof(string)
        && string.Equals(property.GetColumnType(), "jsonb", StringComparison.OrdinalIgnoreCase);

    private static string? ModuleOf(Assembly assembly)
    {
        const string prefix = "LearnStack.Modules.";
        var name = assembly.GetName().Name!;
        return name.StartsWith(prefix, StringComparison.Ordinal) ? name[prefix.Length..name.IndexOf('.', prefix.Length)] : null;
    }

    private static IModel PlantedModel()
    {
        using var context = new PlantedContext();
        return context.GetService<IDesignTimeModel>().Model;
    }

    private sealed record RegisteredLocator(string? Module, IUserReferenceLocator Instance);

    private sealed record ModuleModel(string Module, IModel Model, IReadOnlyCollection<Type> Audited);

    private sealed record PersonReference(string Module, Type Entity, string Path, UserReferenceKind Kind, bool IsKey);
}

/// <summary>A synthetic audited aggregate: a typed, an untyped and a contained person reference, and one the grammar cannot reach.</summary>
internal sealed class PlantedAggregate
{
    public Guid Id { get; set; }
    public UserId Owner { get; set; }
    public Guid DelegateMembershipId { get; set; }
    public List<PlantedMember> Members { get; } = [];
    public PlantedLead? Lead { get; set; }
}

internal sealed class PlantedMember
{
    public Guid Id { get; set; }
    public Guid PlantedAggregateId { get; set; }
    public UserId Reviewer { get; set; }
}

internal sealed class PlantedLead
{
    public Guid Id { get; set; }
    public Guid PlantedAggregateId { get; set; }
    public UserId Approver { get; set; }
}

/// <summary>Declares the planted references wrongly: a wrong kind, a path the model does not hold, another module's entity.</summary>
internal sealed class PlantedLocator : IUserReferenceLocator
{
    public IReadOnlyCollection<UserReferenceDeclaration> Redactable { get; } =
    [
        new(typeof(PlantedAggregate), UserReferencePath.Of(nameof(PlantedAggregate.Owner)), UserReferenceKind.Membership),
        new(typeof(PlantedAggregate), UserReferencePath.Of("Retired"), UserReferenceKind.User),
        new(typeof(IdentityDomain.Membership), UserReferencePath.Of("Planted"), UserReferenceKind.User),
    ];

    public IReadOnlyCollection<RetainedUserReference> Retained { get; } = [];
}

internal sealed class PlantedContext : DbContext
{
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.UseNpgsql("Host=model-only;Database=model-only;Username=model-only");

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PlantedAggregate>(aggregate =>
        {
            aggregate.Property(entity => entity.Owner).HasConversion(id => id.Value, value => UserId.From(value));
            aggregate.HasMany(entity => entity.Members).WithOne().HasForeignKey(member => member.PlantedAggregateId);
            aggregate.HasOne(entity => entity.Lead).WithOne().HasForeignKey<PlantedLead>(lead => lead.PlantedAggregateId);
        });
        modelBuilder.Entity<PlantedMember>().Property(member => member.Reviewer).HasConversion(id => id.Value, value => UserId.From(value));
        modelBuilder.Entity<PlantedLead>().Property(lead => lead.Approver).HasConversion(id => id.Value, value => UserId.From(value));
    }
}
