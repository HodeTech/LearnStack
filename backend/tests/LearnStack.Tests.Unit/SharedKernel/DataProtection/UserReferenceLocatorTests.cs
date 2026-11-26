using FluentAssertions;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identifiers;
using Xunit;

namespace LearnStack.Tests.Unit.SharedKernel.DataProtection;

/// <summary>The locator contract of ADR-0108 § 7: the closed path grammar and the checks a merged set of declarations passes.</summary>
public sealed class UserReferenceLocatorTests
{
    [Fact]
    public void The_grammar_has_three_spellings_and_accepts_only_identifier_names()
    {
        UserReferencePath.Of("CreatedBy").ToString().Should().Be("CreatedBy");
        UserReferencePath.Member("FeatureFlags", "UpdatedBy").ToString().Should().Be("FeatureFlags/*/UpdatedBy");
        UserReferencePath.Elements("MembershipSnapshotJson").ToString().Should().Be("MembershipSnapshotJson/*");

        foreach (var malformed in new[] { "", " ", "Created By", "a/b", "~0", "*", "1Created", "Créé", new string('a', 129) })
        {
            FluentActions.Invoking(() => UserReferencePath.Of(malformed)).Should().Throw<ArgumentException>(malformed);
            FluentActions.Invoking(() => UserReferencePath.Member(malformed, "UpdatedBy")).Should().Throw<ArgumentException>(malformed);
            FluentActions.Invoking(() => UserReferencePath.Elements(malformed)).Should().Throw<ArgumentException>(malformed);
        }

        UserReferencePath.IsName(new string('a', UserReferencePath.MaximumNameLength)).Should().BeTrue();
        FluentActions.Invoking(() => UserReferencePath.Member(null!, "UpdatedBy")).Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void A_declaration_names_a_plain_entity_and_a_defined_kind()
    {
        new UserReferenceDeclaration(typeof(LocatorProbe), UserReferencePath.Of("Owner"), UserReferenceKind.User).EntityName.Should().Be("LocatorProbe");

        FluentActions.Invoking(() => new UserReferenceDeclaration(typeof(List<int>), UserReferencePath.Of("Owner"), UserReferenceKind.User))
            .Should().Throw<ArgumentException>("an audit row's entity_type is a plain CLR name");
        FluentActions.Invoking(() => new UserReferenceDeclaration(typeof(Nested), UserReferencePath.Of("Owner"), UserReferenceKind.User))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new UserReferenceDeclaration(typeof(LocatorProbe), UserReferencePath.Of("Owner"), 0))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_retained_reference_is_an_entity_key_reported_as_the_entity_reference_with_a_reason()
    {
        UserReferenceDeclarations.OwnKey(typeof(LocatorProbe)).ReportedAs.Should().Be(RetainedReferenceKind.AuditEntityReference);

        FluentActions.Invoking(() => new RetainedUserReference(typeof(LocatorProbe), UserReferencePath.Of("Id"),
            RetainedReferenceKind.AuditActorReference, "no")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new RetainedUserReference(typeof(LocatorProbe), UserReferencePath.Member("Items", "Id"),
            RetainedReferenceKind.AuditEntityReference, "no")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new RetainedUserReference(typeof(LocatorProbe), UserReferencePath.Of("Id"),
            RetainedReferenceKind.AuditEntityReference, " ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Attribution_is_declared_once_per_auditable_entity_and_refused_for_anything_else()
    {
        UserReferenceDeclarations.Attribution(typeof(LocatorProbe)).Select(declaration => declaration.Path.ToString())
            .Should().Equal("CreatedBy", "UpdatedBy", "DeletedBy");
        UserReferenceDeclarations.Attribution(typeof(LocatorProbe)).Should().OnlyContain(declaration => declaration.Kind == UserReferenceKind.User);

        FluentActions.Invoking(() => UserReferenceDeclarations.Attribution(typeof(string))).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => UserReferenceDeclarations.Attribution()).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_merged_catalog_refuses_an_empty_locator_a_duplicate_path_a_name_clash_and_a_navigation_clash()
    {
        var owner = new UserReferenceDeclaration(typeof(LocatorProbe), UserReferencePath.Of("Owner"), UserReferenceKind.User);
        var catalog = new UserReferenceCatalog([new Locator([owner, .. UserReferenceDeclarations.Attribution(typeof(LocatorProbe))],
            [UserReferenceDeclarations.OwnKey(typeof(LocatorProbe))])]);
        catalog.Redactable.Select(declaration => declaration.Path.ToString())
            .Should().Equal("CreatedBy", "DeletedBy", "Owner", "UpdatedBy");
        catalog.Retained.Should().ContainSingle();

        Refused(new Locator([], []));
        Refused(new Locator([owner], []), new Locator([owner], []));
        Refused(new Locator([owner, new UserReferenceDeclaration(typeof(LocatorProbe), UserReferencePath.Elements("Owner"), UserReferenceKind.User)], []));
        Refused(new Locator([owner], [new RetainedUserReference(typeof(LocatorProbe), UserReferencePath.Of("Owner"),
            RetainedReferenceKind.AuditEntityReference, "kept")]));
        Refused(new Locator([new UserReferenceDeclaration(typeof(System.Threading.Timer), UserReferencePath.Of("Owner"),
            UserReferenceKind.User)], []), new Locator([new UserReferenceDeclaration(typeof(System.Timers.Timer), UserReferencePath.Of("Lead"),
            UserReferenceKind.User)], []));
        Refused(new Locator([owner, new UserReferenceDeclaration(typeof(LocatorProbe), UserReferencePath.Member("Owner", "Approver"),
            UserReferenceKind.User)], []));
    }

    private static void Refused(params IUserReferenceLocator[] locators) =>
        FluentActions.Invoking(() => new UserReferenceCatalog(locators)).Should().Throw<ArgumentException>();

    private sealed class Locator(IReadOnlyCollection<UserReferenceDeclaration> redactable, IReadOnlyCollection<RetainedUserReference> retained)
        : IUserReferenceLocator
    {
        public IReadOnlyCollection<UserReferenceDeclaration> Redactable { get; } = redactable;

        public IReadOnlyCollection<RetainedUserReference> Retained { get; } = retained;
    }

    private sealed class Nested;
}

/// <summary>An auditable entity for the locator tests: its attribution columns are inherited.</summary>
internal sealed class LocatorProbe : AuditableEntity<UserId>;
