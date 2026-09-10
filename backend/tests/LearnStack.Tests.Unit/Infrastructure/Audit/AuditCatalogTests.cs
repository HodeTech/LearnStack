using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using MediatR;
using LearnStack.SharedKernel.Results;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Audit;

/// <summary>
/// What the catalogue refuses at startup, and what it must not.
/// </summary>
/// <remarks>
/// <para>
/// Driven through the public <see cref="AuditCatalog"/> constructor rather than the
/// internal builder, because that constructor is the composition root's only call and the
/// refusals are startup failures: a deployment that boots with a broken catalogue is the
/// thing they exist to prevent.
/// </para>
/// <para>
/// Every one of these guards was unconstrained when it shipped — the unit suite used a
/// catalogue double and the integration suite built the real one only through the happy
/// path, so any of the five could have been deleted with the whole suite green. That is
/// the mirror of this repository's Packet 5 lesson: there, tests agreed with the code;
/// here there were none to agree.
/// </para>
/// </remarks>
public sealed class AuditCatalogTests
{
    public sealed record AlphaCommand : IRequest<Result<string>>;

    public sealed record BetaCommand : IRequest<Result<string>>;

    [Fact]
    public void A_request_type_may_declare_several_operations_in_declaration_order()
    {
        // ProvisionTenantCommand's shape: two aggregate roots on one transaction, both
        // MUST. The order is the flush order of the rows, so it is part of the contract.
        var catalog = Catalog(new Source("tenancy", builder => builder
            .MustAudit<AlphaCommand>("tenancy.tenant.create", OperationType.Create, typeof(string))
            .MustAudit<AlphaCommand>("tenancy.organization.create", OperationType.Create, typeof(string))));

        catalog.TryGet(typeof(AlphaCommand), out var registration).Should().BeTrue();
        registration.WritesNoRow.Should().BeFalse();
        registration.Entries.Select(entry => entry.Operation).Should().Equal(
            "tenancy.tenant.create", "tenancy.organization.create");
    }

    [Fact]
    public void Two_request_types_may_raise_the_same_slug()
    {
        // Shipped and legal: an organization is created inside provisioning AND alone, and
        // the matrix carries one row per operation rather than one per caller. A duplicate
        // check on the slug would have broken the seed on the day it was written.
        var catalog = Catalog(new Source("tenancy", builder => builder
            .MustAudit<AlphaCommand>("tenancy.organization.create", OperationType.Create, typeof(string))
            .MustAudit<BetaCommand>("tenancy.organization.create", OperationType.Create, typeof(string))));

        catalog.TryGet(typeof(AlphaCommand), out _).Should().BeTrue();
        catalog.TryGet(typeof(BetaCommand), out _).Should().BeTrue();
        catalog.All.Should().HaveCount(2);
    }

    [Fact]
    public void An_unregistered_request_is_not_found()
    {
        // The rejection, and the reason TryGet is not a count: `Off` is registered and
        // silent, which is a decision; unregistered is an omission, and only the second is
        // refused at runtime.
        var catalog = Catalog(new Source("tenancy", builder => builder.Off<AlphaCommand>()));

        catalog.TryGet(typeof(BetaCommand), out _).Should().BeFalse();

        catalog.TryGet(typeof(AlphaCommand), out var silent).Should().BeTrue();
        silent.WritesNoRow.Should().BeTrue();
        silent.Entries.Should().BeEmpty();
    }

    [Theory]
    // The shape ADR-0044 § 6 fixes: three segments, lowercase, snake_case within a
    // segment. A slug that fails it reads to the matrix join as a MISSING ROW rather than
    // as a typo, which is the harder failure to diagnose — so it is refused at startup.
    [InlineData("tenancy.tenant")]
    [InlineData("tenancy.tenant.create.extra")]
    [InlineData("Tenancy.Tenant.Create")]
    [InlineData("tenancy.tenant-assertion.reject")]
    [InlineData("tenancy..create")]
    [InlineData("1tenancy.tenant.create")]
    // The trailing newline, which .NET's `$` admits and `\z` does not. It reads correctly
    // in every log line and joins against nothing.
    [InlineData("tenancy.tenant.create\n")]
    public void A_slug_that_is_not_the_fixed_shape_is_refused(string slug)
    {
        var build = () => Catalog(new Source("tenancy", builder =>
            builder.MustAudit<AlphaCommand>(slug, OperationType.Create, typeof(string))));

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*{module}.{resource}.{verb}*");
    }

    [Fact]
    public void A_slug_whose_module_segment_is_not_the_declaring_module_is_refused()
    {
        // ENFORCED rather than trusted. The join reads the declaring module's matrix, so a
        // mismatched prefix sends it to the wrong file — and reports the absence as that
        // module's omission.
        var build = () => Catalog(new Source("tenancy", builder =>
            builder.MustAudit<AlphaCommand>(
                "customization.content_type.register", OperationType.Create, typeof(string))));

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*module segment*");
    }

    [Fact]
    public void An_off_path_slug_is_exempt_from_the_module_prefix()
    {
        // And has to be: platform.admin_scope.enter belongs to no module's request path,
        // and its matrix row lives in Tenancy's file because that is where a reader looks.
        // Requiring the prefix would make it unregistrable from the only source that could
        // declare it.
        var catalog = Catalog(new Source("tenancy", builder => builder.DeclareOffPath(
            "platform.admin_scope.enter", OperationType.SecurityEvent, OperationClass.Must)));

        var entry = catalog.All.Should().ContainSingle().Subject;
        entry.Operation.Should().Be("platform.admin_scope.enter");
        entry.ModuleName.Should().Be("platform", "an off-path entry takes the slug's own segment");
    }

    [Fact]
    public void One_off_path_slug_may_not_be_declared_twice()
    {
        // An off-path operation is addressed BY SLUG — that is the whole of its
        // addressing, there being no request type — so a second declaration is two
        // modules disagreeing about one operation's tier while the writer silently takes
        // whichever landed first. The request-keyed side has refused a double claim since
        // it was written; this side is addressed the same way and owes the same refusal.
        var build = () => Catalog(
            new Source("tenancy", builder => builder.DeclareOffPath(
                "platform.admin_scope.enter", OperationType.SecurityEvent, OperationClass.Must)),
            new Source("customization", builder => builder.DeclareOffPath(
                "platform.admin_scope.enter", OperationType.SecurityEvent, OperationClass.May)));

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*declared off-path more than once*");
    }

    [Fact]
    public void An_off_path_slug_is_found_by_its_own_name_and_a_request_keyed_one_is_not()
    {
        // The lookup a scope, a middleware or a provider uses, because there is no request
        // type to key on. It answers for off-path entries and ONLY those: reaching a
        // request-keyed slug from a non-request writer would put a second writer on a row
        // the pipeline already writes, and neither row would agree about the outcome.
        var catalog = Catalog(new Source("tenancy", builder => builder
            .DeclareOffPath(
                "platform.admin_scope.enter", OperationType.SecurityEvent, OperationClass.Must)
            .MustAudit<AlphaCommand>("tenancy.tenant.create", OperationType.Create, typeof(string))));

        catalog.TryGetOffPath("platform.admin_scope.enter", out var declared).Should().BeTrue();
        declared.OperationClass.Should().Be(OperationClass.Must);

        catalog.TryGetOffPath("tenancy.tenant.create", out _).Should().BeFalse(
            "a request-keyed slug is the pipeline's to write, not an off-path writer's");
    }

    [Fact]
    public void Two_modules_may_not_claim_one_request_type()
    {
        // It would give the type two classifications and no rule for choosing — and the
        // loser's matrix row would then read as the OTHER module's omission.
        var build = () => Catalog(
            new Source("tenancy", builder => builder
                .MustAudit<AlphaCommand>("tenancy.tenant.create", OperationType.Create, typeof(string))),
            new Source("customization", builder => builder
                .MustAudit<AlphaCommand>(
                    "customization.content_type.register", OperationType.Create, typeof(string))));

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*registered by both*");
    }

    [Fact]
    public void A_type_registered_off_may_not_then_be_audited()
    {
        var build = () => Catalog(new Source("tenancy", builder => builder
            .Off<AlphaCommand>()
            .MustAudit<AlphaCommand>("tenancy.tenant.create", OperationType.Create, typeof(string))));

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*already registered Off*");
    }

    [Fact]
    public void An_audited_type_may_not_then_be_registered_off()
    {
        // The mirror. Silent and audited are different answers, and a type cannot be both
        // — whichever order the two calls arrive in.
        var build = () => Catalog(new Source("tenancy", builder => builder
            .MustAudit<AlphaCommand>("tenancy.tenant.create", OperationType.Create, typeof(string))
            .Off<AlphaCommand>()));

        build.Should().Throw<InvalidOperationException>()
            .WithMessage("*Off after declaring*");
    }

    [Fact]
    public void The_merge_is_ordered_by_module_whatever_order_the_sources_arrive_in()
    {
        // `All` feeds the architecture rule that compares the catalogue against the
        // matrices, and a rule whose input order varies between runs is a rule that fails
        // differently on a second run.
        var forward = Catalog(
            new Source("customization", builder => builder.MustAudit<BetaCommand>(
                "customization.content_type.register", OperationType.Create, typeof(string))),
            new Source("tenancy", builder => builder.MustAudit<AlphaCommand>(
                "tenancy.tenant.create", OperationType.Create, typeof(string))));

        var reversed = Catalog(
            new Source("tenancy", builder => builder.MustAudit<AlphaCommand>(
                "tenancy.tenant.create", OperationType.Create, typeof(string))),
            new Source("customization", builder => builder.MustAudit<BetaCommand>(
                "customization.content_type.register", OperationType.Create, typeof(string))));

        forward.All.Select(entry => entry.Operation)
            .Should().Equal(reversed.All.Select(entry => entry.Operation));
    }

    private static AuditCatalog Catalog(params IAuditCatalogSource[] sources) => new(sources);

    private sealed class Source(string moduleName, Action<IAuditCatalogBuilder> describe)
        : IAuditCatalogSource
    {
        public string ModuleName => moduleName;

        public void Describe(IAuditCatalogBuilder builder) => describe(builder);
    }
}
