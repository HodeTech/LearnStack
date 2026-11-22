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
/// A reference the grammar cannot express (inside a contained single navigation, one level deeper, or in a contained
/// member's own key, which the capture writes as a JSON object key and a <c>changes</c> path segment) fails the census
/// until the grammar grows. An array of identifiers held in a <c>jsonb</c> property is invisible to a type census, so an
/// <c>Elements</c> declaration is instead checked to name a mapped <c>jsonb</c> property.
/// </para>
/// <para>
/// <b>No unclassified identifier.</b> A name is a weak signal, so every other plain <see cref="Guid"/> of an audited
/// entity or of what it contains fails as an unclassified identifier unless it is its entity's or member's own
/// single-column key or is on <see cref="NotAPerson"/>, the explicit list of identifiers that name no person. A retained
/// reference must be its entity's whole single-column key: a part of a composite key is not the row's
/// <c>entity_id</c>.
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

        // Planted: a synthetic module whose audited entities hold a typed, an untyped and a contained person reference, a
        // plain Guid no name classifies, a contained collection keyed by a person and a composite key holding a person,
        // and that registers no locator; then one that declares them with a wrong kind, a stale path and another module's
        // entity, and retains the person part of the composite key as if it were the entity's own key.
        var planted = new ModuleModel("Planted", PlantedModel(), [typeof(PlantedAggregate), typeof(PlantedGrant)]);
        var unplanted = Offenders([.. models, planted], locators, catalog);
        unplanted.Should()
            .Contain("Missing user reference locator: Planted")
            .And.Contain("Undeclared person reference: PlantedAggregate.Owner")
            .And.Contain("Undeclared person reference: PlantedAggregate.DelegateMembershipId")
            .And.Contain("Undeclared person reference: PlantedAggregate.Members/*/Reviewer")
            .And.Contain("Person reference outside the path grammar: PlantedAggregate.Lead/Approver")
            .And.Contain("Unclassified identifier: PlantedAggregate.ReviewedBy")
            .And.Contain("Unclassified identifier: PlantedGrant.ScopeId")
            .And.Contain("Person reference in a contained member's key, outside the path grammar: PlantedAggregate.Seats/Holder")
            .And.Contain("Undeclared person reference: PlantedGrant.Holder");
        unplanted.Should().NotContain(offender => offender.Contains(".Members/*/Id", StringComparison.Ordinal)
            || offender.Contains(".Lead/Id", StringComparison.Ordinal) || offender.EndsWith("PlantedAggregate.Id", StringComparison.Ordinal),
            "an entity's or a contained member's own single-column key is its identity, not a reference");
        var wrong = new RegisteredLocator("Planted", new PlantedLocator());
        Offenders([.. models, planted], [.. locators, wrong],
                new UserReferenceCatalog([.. locators.Select(locator => locator.Instance), wrong.Instance])).Should()
            .Contain("Wrong kind for PlantedAggregate.Owner: Membership, the model holds User")
            .And.Contain("Stale declaration: PlantedAggregate.Retired")
            .And.Contain("Locator of Planted declares another module's entity: Membership")
            .And.Contain("Retained reference is not the entity's own key: PlantedGrant.Holder")
            .And.Contain("Person reference in a contained member's key, outside the path grammar: PlantedAggregate.Seats/Holder",
                "a declared member key is still refused: the key text stays in the JSON object key and the changes path")
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

    /// <summary>
    /// The plain <see cref="Guid"/> properties of audited entities that name no person, each with why. Every other plain
    /// <see cref="Guid"/> that is not its entity's (or contained member's) own single-column key is a person reference
    /// until it is declared or listed here, so a <c>ReviewedBy</c> added as a <see cref="Guid"/> fails the census. Read
    /// from the models: strongly typed identifiers (tenant, organization, role, definitions) are not plain
    /// <see cref="Guid"/>s and need no entry.
    /// </summary>
    private static readonly Dictionary<string, string> NotAPerson = new(StringComparer.Ordinal)
    {
        ["MembershipFieldConsent.DefinitionId"] = "the custom field definition the consent is for, a tenant definition",
    };

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

        var identifiers = models.SelectMany(model => model.Identifiers).ToHashSet(StringComparer.Ordinal);
        offenders.AddRange(identifiers.Where(identifier => !NotAPerson.ContainsKey(identifier)).Order(StringComparer.Ordinal)
            .Select(identifier => "Unclassified identifier: " + identifier));
        offenders.AddRange(NotAPerson.Keys.Where(identifier => !identifiers.Contains(identifier)).Order(StringComparer.Ordinal)
            .Select(identifier => "Stale identifier allowance: " + identifier));

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

            var ownKey = OwnKey(entity.FindPrimaryKey()?.Properties ?? []);
            foreach (var property in entity.GetProperties())
            {
                if (KindOf(property) is { } kind)
                {
                    yield return new PersonReference(model.Module, type, property.Name, kind, property == ownKey);
                }
                else if (IsIdentifier(property) && property != ownKey)
                {
                    model.Identifiers.Add(type.Name + "." + property.Name);
                }
            }

            foreach (var navigation in Containments(entity))
            {
                foreach (var reference in Contained(model, type, navigation, navigation.Name, depth: 1, offenders))
                {
                    yield return reference;
                }
            }
        }
    }

    /// <summary>A key's one property, or none when the key is composite: only a whole single-column key is an own key.</summary>
    private static IReadOnlyProperty? OwnKey(IReadOnlyList<IReadOnlyProperty> key) => key.Count == 1 ? key[0] : null;

    /// <summary>A plain <see cref="Guid"/>: an identifier no type names, which only its declaration or an allowance classifies.</summary>
    private static bool IsIdentifier(IReadOnlyProperty property) =>
        (Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType) == typeof(Guid);

    private static IEnumerable<PersonReference> Contained(ModuleModel model, Type owner, IReadOnlyNavigation navigation, string path,
        int depth, List<string> offenders)
    {
        var member = navigation.TargetEntityType;

        // The member's own key, as the capture computes it: its key without the foreign key to the owner. A collection's
        // members are keyed by it as JSON object keys and as segments of each changes path, which no declaration reaches.
        var local = (member.FindPrimaryKey()?.Properties ?? []).Where(property => !navigation.ForeignKey.Properties.Contains(property))
            .ToList();
        var ownKey = OwnKey(local);
        var shape = depth == 1 && navigation.IsCollection ? "/*/" : "/";
        foreach (var property in member.GetProperties().Where(property => !navigation.ForeignKey.Properties.Contains(property)))
        {
            if (KindOf(property) is not { } kind)
            {
                if (IsIdentifier(property) && property != ownKey)
                {
                    model.Identifiers.Add(owner.Name + "." + path + shape + property.Name);
                }

                continue;
            }

            if (navigation.IsCollection && local.Contains(property))
            {
                offenders.Add($"Person reference in a contained member's key, outside the path grammar: {owner.Name}.{path}/{property.Name}");
            }
            else if (depth == 1 && navigation.IsCollection)
            {
                yield return new PersonReference(model.Module, owner, UserReferencePath.Member(navigation.Name, property.Name).ToString(),
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
                foreach (var reference in Contained(model, owner, inner, path + "/" + inner.Name, depth + 1, offenders))
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

    private sealed record ModuleModel(string Module, IModel Model, IReadOnlyCollection<Type> Audited)
    {
        /// <summary>The plain <see cref="Guid"/> paths the census found that no type classifies, filled as it walks.</summary>
        public HashSet<string> Identifiers { get; } = new(StringComparer.Ordinal);
    }

    private sealed record PersonReference(string Module, Type Entity, string Path, UserReferenceKind Kind, bool IsKey);
}

/// <summary>
/// A synthetic audited aggregate: a typed, an untyped and a contained person reference, one the grammar cannot reach, a
/// plain <see cref="Guid"/> no name classifies, and a contained collection keyed by a person.
/// </summary>
internal sealed class PlantedAggregate
{
    public Guid Id { get; set; }
    public UserId Owner { get; set; }
    public Guid DelegateMembershipId { get; set; }
    public Guid ReviewedBy { get; set; }
    public List<PlantedMember> Members { get; } = [];
    public PlantedLead? Lead { get; set; }
    public List<PlantedSeat> Seats { get; } = [];
}

/// <summary>A contained member keyed by a person: its key is the JSON object key and a changes path segment.</summary>
internal sealed class PlantedSeat
{
    public Guid PlantedAggregateId { get; set; }
    public UserId Holder { get; set; }
    public int Row { get; set; }
}

/// <summary>A synthetic audited aggregate whose composite key holds a person: no part of it is the entity's own key.</summary>
internal sealed class PlantedGrant
{
    public Guid ScopeId { get; set; }
    public UserId Holder { get; set; }
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

/// <summary>
/// Declares the planted references wrongly: a wrong kind, a path the model does not hold, another module's entity, and a
/// retained part of a composite key.
/// </summary>
internal sealed class PlantedLocator : IUserReferenceLocator
{
    public IReadOnlyCollection<UserReferenceDeclaration> Redactable { get; } =
    [
        new(typeof(PlantedAggregate), UserReferencePath.Of(nameof(PlantedAggregate.Owner)), UserReferenceKind.Membership),
        new(typeof(PlantedAggregate), UserReferencePath.Of("Retired"), UserReferenceKind.User),
        new(typeof(IdentityDomain.Membership), UserReferencePath.Of("Planted"), UserReferenceKind.User),
    ];

    public IReadOnlyCollection<RetainedUserReference> Retained { get; } =
    [
        new(typeof(PlantedGrant), UserReferencePath.Of(nameof(PlantedGrant.Holder)), RetainedReferenceKind.AuditEntityReference,
            "Planted: the person part of a composite key is not the row's entity_id."),
    ];
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
            aggregate.HasMany(entity => entity.Seats).WithOne().HasForeignKey(seat => seat.PlantedAggregateId);
        });
        modelBuilder.Entity<PlantedSeat>(seat =>
        {
            seat.Property(member => member.Holder).HasConversion(id => id.Value, value => UserId.From(value));
            seat.HasKey(member => new { member.PlantedAggregateId, member.Holder });
        });
        modelBuilder.Entity<PlantedGrant>(grant =>
        {
            grant.Property(entity => entity.Holder).HasConversion(id => id.Value, value => UserId.From(value));
            grant.HasKey(entity => new { entity.ScopeId, entity.Holder });
        });
        modelBuilder.Entity<PlantedMember>().Property(member => member.Reviewer).HasConversion(id => id.Value, value => UserId.From(value));
        modelBuilder.Entity<PlantedLead>().Property(lead => lead.Approver).HasConversion(id => id.Value, value => UserId.From(value));
    }
}
