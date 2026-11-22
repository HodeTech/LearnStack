using System.Collections.Immutable;
using LearnStack.SharedKernel.Domain;

namespace LearnStack.SharedKernel.DataProtection;

/// <summary>
/// One module's declaration of where its audited entities hold a person or Membership reference in an audit row's
/// <c>before_state</c>, <c>after_state</c> and <c>changes</c>
/// (<see href="../../../../docs/decisions/0108-privileged-audit-redaction.md">ADR-0108 § 7</see>).
/// </summary>
/// <remarks>
/// <para>
/// <b>A declaration, not an actor.</b> A locator runs no SQL, holds no privilege and never names the audit table: the
/// Audit-owned executor behind <c>IAuditRedactor</c> applies every module's declarations on the platform scope. That
/// reconciles Audit Subsystem § 10 (the module knows where its references are) with Data Protection (the module never
/// reaches the log). The composition root collects the implementations, as it collects <c>IAuditCatalogSource</c>;
/// no module references another module's locator.
/// </para>
/// <para>
/// <b>Every reference is either redactable or retained.</b> The architecture census reads each module's mapped
/// model: every person-reference property of an audited entity, and of what that entity contains, is in exactly one
/// of the two lists, and every path a module declares has a canary that embeds a reference there and proves the
/// executor removes it.
/// </para>
/// </remarks>
public interface IUserReferenceLocator
{
    /// <summary>The references the redaction replaces when they hold the request's subject or one of its Memberships.</summary>
    IReadOnlyCollection<UserReferenceDeclaration> Redactable { get; }

    /// <summary>The references deliberately kept, each with the reported kind that already counts it and the reason.</summary>
    IReadOnlyCollection<RetainedUserReference> Retained { get; }
}

/// <summary>What a declared reference holds, which fixes the identifiers it is compared with (ADR-0108 § 5).</summary>
/// <remarks>Zero is deliberately undefined, so an unassigned kind is never a valid one.</remarks>
public enum UserReferenceKind
{
    /// <summary>A person's <c>UserId</c>: compared with the request's subject.</summary>
    User = 1,

    /// <summary>A Membership identifier: compared with the request's frozen Membership snapshot.</summary>
    Membership = 2,
}

/// <summary>The closed path grammar: where a reference sits inside one entity's captured snapshot.</summary>
/// <remarks>
/// The three forms are what the capture writes (<c>CapturedEntityChange</c>, ADR-0044 Amendment 6 §§ 4, 5): a
/// property of the entity, a property of each member of a collection the entity contains (keyed by the member's own
/// key, never an array index), and a property whose stored value is an array of identifiers. A deeper or another
/// shape is not expressible, so a model that adds one fails the census until the grammar grows by decision.
/// </remarks>
public enum UserReferenceShape
{
    /// <summary><c>Name</c>: the entity's own property.</summary>
    Property = 1,

    /// <summary><c>Navigation/*/Name</c>: the property of every member of a contained collection.</summary>
    Member = 2,

    /// <summary><c>Name/*</c>: every element of a property whose value is an array of identifiers.</summary>
    Elements = 3,
}

/// <summary>One path of the closed grammar, built only through its three factories.</summary>
/// <remarks>
/// A name is an EF model property or navigation name: an ASCII identifier, so it is one RFC 6901 reference token
/// that needs no escaping and spells the same in a snapshot key and in a <c>changes</c> path.
/// </remarks>
public sealed record UserReferencePath
{
    /// <summary>The longest name a path accepts: an identifier, never a document.</summary>
    public const int MaximumNameLength = 128;

    private UserReferencePath(UserReferenceShape shape, string? navigation, string property)
    {
        if (!IsName(property) || (navigation is not null && !IsName(navigation)))
        {
            throw new ArgumentException("A reference path names an identifier property and navigation.", nameof(property));
        }

        Shape = shape;
        Navigation = navigation;
        Property = property;
    }

    public UserReferenceShape Shape { get; }

    /// <summary>The contained collection's navigation name; <see langword="null"/> unless <see cref="Shape"/> is <see cref="UserReferenceShape.Member"/>.</summary>
    public string? Navigation { get; }

    /// <summary>The property that holds the reference.</summary>
    public string Property { get; }

    /// <summary>The entity's own property: <c>Name</c>.</summary>
    /// <exception cref="ArgumentException">The name is not an identifier.</exception>
    public static UserReferencePath Of(string property) => new(UserReferenceShape.Property, null, property);

    /// <summary>The property of every member of a contained collection: <c>Navigation/*/Name</c>.</summary>
    /// <exception cref="ArgumentException">A name is not an identifier.</exception>
    public static UserReferencePath Member(string navigation, string property)
    {
        ArgumentNullException.ThrowIfNull(navigation);
        return new(UserReferenceShape.Member, navigation, property);
    }

    /// <summary>Every element of a property whose value is an array of identifiers: <c>Name/*</c>.</summary>
    /// <exception cref="ArgumentException">The name is not an identifier.</exception>
    public static UserReferencePath Elements(string property) => new(UserReferenceShape.Elements, null, property);

    /// <summary>The path's spelling in the grammar: <c>Name</c>, <c>Navigation/*/Name</c> or <c>Name/*</c>.</summary>
    public override string ToString() => Shape switch
    {
        UserReferenceShape.Member => Navigation + "/*/" + Property,
        UserReferenceShape.Elements => Property + "/*",
        _ => Property,
    };

    /// <summary>Whether a value is a name the grammar accepts: an ASCII identifier of bounded length.</summary>
    public static bool IsName(string? value) =>
        value is { Length: > 0 and <= MaximumNameLength }
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}

/// <summary>One redactable reference: an audited entity, a path into its snapshot, and what it holds.</summary>
/// <remarks>
/// The entity is matched by its CLR name, because that is what an audit row's <c>entity_type</c> and a
/// <c>changes</c> path carry (ADR-0044 Amendment 5 § 2), so a generic or nested name is refused.
/// </remarks>
public sealed record UserReferenceDeclaration
{
    /// <exception cref="ArgumentException">The entity type has no plain identifier name, or the kind is undefined.</exception>
    public UserReferenceDeclaration(Type entityType, UserReferencePath path, UserReferenceKind kind)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(path);
        if (!IsEntityName(entityType) || !Enum.IsDefined(kind))
        {
            throw new ArgumentException("A reference declaration names a plain entity type and a defined kind.", nameof(entityType));
        }

        EntityType = entityType;
        Path = path;
        Kind = kind;
    }

    public Type EntityType { get; }

    public UserReferencePath Path { get; }

    public UserReferenceKind Kind { get; }

    /// <summary>The name an audit row carries in <c>entity_type</c> and in each <c>changes</c> path.</summary>
    public string EntityName => EntityType.Name;

    internal static bool IsEntityName(Type type) =>
        !type.IsGenericType && !type.IsNested && UserReferencePath.IsName(type.Name);
}

/// <summary>A person reference deliberately kept by the redaction, with the reported kind that already counts it.</summary>
/// <remarks>
/// Only an entity's own key qualifies: a payload copy of the row's <c>entity_id</c>, which ADR-0108 § 6 retains and
/// the report counts as <see cref="RetainedReferenceKind.AuditEntityReference"/>. Redacting the copy would hide
/// nothing the row's own column does not still show.
/// </remarks>
public sealed record RetainedUserReference
{
    /// <exception cref="ArgumentException">
    /// The entity type has no plain name, the path is not a property, the kind is not the entity reference, or the
    /// reason is blank.
    /// </exception>
    public RetainedUserReference(Type entityType, UserReferencePath path, RetainedReferenceKind reportedAs, string reason)
    {
        ArgumentNullException.ThrowIfNull(entityType);
        ArgumentNullException.ThrowIfNull(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (!UserReferenceDeclaration.IsEntityName(entityType) || path.Shape != UserReferenceShape.Property
            || reportedAs != RetainedReferenceKind.AuditEntityReference)
        {
            throw new ArgumentException("A retained reference is an entity's own key, reported as the audit entity reference.", nameof(path));
        }

        EntityType = entityType;
        Path = path;
        ReportedAs = reportedAs;
        Reason = reason;
    }

    public Type EntityType { get; }

    public UserReferencePath Path { get; }

    /// <summary>The closed kind under which the redaction report counts the rows this reference survives in.</summary>
    public RetainedReferenceKind ReportedAs { get; }

    /// <summary>Why the reference is kept: one line a reviewer reads.</summary>
    public string Reason { get; }

    public string EntityName => EntityType.Name;
}

/// <summary>The shared declarations every module repeats, so none of them is written per entity by hand.</summary>
public static class UserReferenceDeclarations
{
    /// <summary>The attribution columns every <see cref="AuditableEntity{TId}"/> carries into its snapshot.</summary>
    public static readonly ImmutableArray<string> AttributionProperties = ["CreatedBy", "UpdatedBy", "DeletedBy"];

    /// <summary>
    /// The creator, updater and deleter of each named entity as redactable <see cref="UserReferenceKind.User"/>
    /// references: the capture keeps them in every snapshot and diff (ADR-0107 § 6 retains the table columns as
    /// <c>attribution_reference</c>; the audit payload copies are not on ADR-0108 § 6's list of survivors).
    /// </summary>
    /// <exception cref="ArgumentException">A named type is not an <see cref="AuditableEntity{TId}"/>.</exception>
    public static IEnumerable<UserReferenceDeclaration> Attribution(params Type[] auditableEntities)
    {
        ArgumentNullException.ThrowIfNull(auditableEntities);
        if (auditableEntities.Length == 0 || !auditableEntities.All(IsAuditable))
        {
            throw new ArgumentException("Attribution is declared for auditable entities.", nameof(auditableEntities));
        }

        return
        [
            .. auditableEntities.SelectMany(entity => AttributionProperties.Select(property =>
                new UserReferenceDeclaration(entity, UserReferencePath.Of(property), UserReferenceKind.User))),
        ];
    }

    /// <summary>The entity's own key, retained because the row's <c>entity_id</c> carries the same value.</summary>
    public static RetainedUserReference OwnKey(Type entityType) =>
        new(entityType, UserReferencePath.Of("Id"), RetainedReferenceKind.AuditEntityReference,
            "The entity's own key is the row's entity_id, which ADR-0108 § 6 retains; redacting the payload copy hides nothing.");

    private static bool IsAuditable(Type? type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(AuditableEntity<>))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Every registered locator's declarations, merged once and checked: the set the redaction applies and the census
/// reads (ADR-0108 § 7).
/// </summary>
/// <remarks>
/// Construction refuses a locator that declares nothing, a path declared twice (redactable or retained, by one
/// module or two), two CLR types that share a name (a row's <c>entity_type</c> could not tell them apart), and an
/// entity whose navigation and property names collide, so a malformed set fails where it is built rather than at
/// the first row it misreads.
/// </remarks>
public sealed class UserReferenceCatalog
{
    /// <exception cref="ArgumentException">The locators' declarations are empty, duplicated or ambiguous.</exception>
    public UserReferenceCatalog(IEnumerable<IUserReferenceLocator> locators)
    {
        ArgumentNullException.ThrowIfNull(locators);
        var redactable = new List<UserReferenceDeclaration>();
        var retained = new List<RetainedUserReference>();
        foreach (var locator in locators)
        {
            ArgumentNullException.ThrowIfNull(locator);
            if (locator.Redactable is not { Count: > 0 } declared || locator.Retained is null
                || declared.Any(declaration => declaration is null) || locator.Retained.Any(reference => reference is null))
            {
                throw new ArgumentException("A user reference locator declares at least one redactable reference.", nameof(locators));
            }

            redactable.AddRange(declared);
            retained.AddRange(locator.Retained);
        }

        var entries = redactable.Select(declaration => (declaration.EntityType, declaration.Path))
            .Concat(retained.Select(reference => (reference.EntityType, reference.Path)))
            .ToList();
        var paths = entries.Select(entry => (entry.EntityType.Name, entry.Path.Navigation, entry.Path.Property)).ToList();
        if (paths.Distinct().Count() != paths.Count)
        {
            throw new ArgumentException("A reference path is declared twice.", nameof(locators));
        }

        if (entries.GroupBy(entry => entry.EntityType.Name, StringComparer.Ordinal)
            .Any(group => group.Select(entry => entry.EntityType).Distinct().Count() > 1))
        {
            throw new ArgumentException("Two declared entity types share a name an audit row cannot tell apart.", nameof(locators));
        }

        if (entries.GroupBy(entry => entry.EntityType.Name, StringComparer.Ordinal).Any(group =>
            group.Where(entry => entry.Path.Navigation is not null).Select(entry => entry.Path.Navigation!)
                .Intersect(group.Where(entry => entry.Path.Navigation is null).Select(entry => entry.Path.Property),
                    StringComparer.Ordinal)
                .Any()))
        {
            throw new ArgumentException("A navigation and a property of one entity share a name.", nameof(locators));
        }

        Redactable = [.. redactable.OrderBy(declaration => declaration.EntityName, StringComparer.Ordinal)
            .ThenBy(declaration => declaration.Path.ToString(), StringComparer.Ordinal)];
        Retained = [.. retained.OrderBy(reference => reference.EntityName, StringComparer.Ordinal)
            .ThenBy(reference => reference.Path.ToString(), StringComparer.Ordinal)];
    }

    /// <summary>Every redactable reference, ordered by entity name and path.</summary>
    public ImmutableArray<UserReferenceDeclaration> Redactable { get; }

    /// <summary>Every retained reference, ordered by entity name and path.</summary>
    public ImmutableArray<RetainedUserReference> Retained { get; }
}
