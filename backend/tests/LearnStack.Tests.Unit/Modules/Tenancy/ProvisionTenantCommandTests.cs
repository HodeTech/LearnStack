using FluentAssertions;
using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using TenantAggregate = LearnStack.Modules.Tenancy.Domain.Tenant;

namespace LearnStack.Tests.Unit.Modules.Tenancy;

/// <summary>
/// The one operation
/// <see href="../../../../docs/decisions/0042-cross-aggregate-provisioning-transaction.md">ADR-0042</see>
/// sanctions to write two aggregate roots on one transaction: what it writes, in
/// what order, and what it refuses before a transaction is ever opened.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <c>IRequestHandler</c> resolved from a container rather than by
/// constructing the handler, for two reasons. The handler is <c>internal</c>, and
/// widening that to reach it from a test would be a visibility change made for the
/// test's convenience. And resolution is itself part of what needs proving: the
/// composition root discovers this handler by assembly scan, so a handler that
/// compiles but is not discoverable is a 500 at the first call, and a test holding
/// a hand-constructed instance would never see it.
/// </para>
/// <para>
/// The database half — that the three writes commit together as
/// <c>learnstack_app</c> under the announced tenant, and that a partial failure
/// leaves nothing — is in <c>TenantProvisioningTests</c>, because it is a claim
/// about policies and a transaction and cannot be made against fakes.
/// </para>
/// </remarks>
public sealed class ProvisionTenantCommandTests
{
    private static readonly FixedClock Clock = new(
        new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero));

    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("0199a000-0000-7000-8000-000000000001"));

    private static readonly OrganizationId Organization =
        OrganizationId.From(Guid.Parse("0199a000-0000-7000-8000-0000000000a1"));

    private static ProvisionTenantCommand Command() => new(
        Tenant, "demo-english", "Demo English",
        Organization, "hq", "Head Office");

    [Fact]
    public async Task Provisioning_writes_the_tenant_then_the_organization_then_the_assignment()
    {
        // The order is not stylistic. `organizations` carries a composite foreign key
        // to (tenant_id, id), so the tenant row has to exist first; and
        // `tenants.default_organization_id` points at a row that does not exist when
        // the tenant is inserted, so the back-reference has to be a second write. A
        // reordering here compiles and passes every fake-free assertion — it fails
        // only against the real schema, which is why the sequence is pinned.
        var (sender, writes) = Build();

        var result = await sender.Send(Command());

        result.IsSuccess.Should().BeTrue();
        writes.Should().Equal(
            "tenant:add", "organization:add", "tenant:update");
    }

    [Fact]
    public async Task The_tenant_carries_its_default_organization_by_the_time_it_is_updated()
    {
        // Asserting the sequence alone would pass with an update that wrote nothing
        // new — the property that matters is the state the second write carries.
        var (sender, _) = Build(out var stores);

        await sender.Send(Command());

        stores.Updated.Should().ContainSingle().Which
            .DefaultOrganizationId.Should().Be(Organization);
    }

    [Fact]
    public async Task The_organization_is_created_inside_the_tenant_being_provisioned()
    {
        // The default organization belongs to the new tenant, not to whatever tenant
        // happened to be ambient. Under the shipped policies a mismatch is a 42501
        // rather than a cross-tenant write, so this is a fail-fast on the way to a
        // refusal — but the refusal is a rollback of the whole provisioning, and the
        // caller sees "provisioning failed" with no hint why.
        var (sender, _) = Build(out var stores);

        await sender.Send(Command());

        stores.Added.Should().ContainSingle().Which.TenantId.Should().Be(Tenant);
    }

    [Fact]
    public async Task The_result_names_both_rows_the_caller_now_has()
    {
        // The caller generated both ids, so what the response adds is confirmation of
        // what was stored under them — including the slugs, which the aggregates accept
        // verbatim rather than normalizing. Measured: `Tenant.Create` REFUSES
        // "Demo-English" rather than lowercasing it, so a caller that expected
        // normalization gets a refusal, not a surprise row.
        var (sender, _) = Build();

        var result = await sender.Send(Command());

        result.IsSuccess.Should().BeTrue();
        var provisioned = result.Value!;
        provisioned.TenantId.Should().Be(Tenant.Value);
        provisioned.Slug.Should().Be("demo-english");
        provisioned.DefaultOrganizationId.Should().Be(Organization.Value);
        provisioned.DefaultOrganizationSlug.Should().Be("hq");
    }

    [Fact]
    public async Task Provisioning_is_attributed_to_the_system_actor()
    {
        // There is nobody in the tenant to attribute it to: provisioning runs before
        // any membership exists. `created_by` is `NOT NULL` and carries no foreign
        // key — deliberately, so an erased actor stays an orphan surrogate rather
        // than becoming unreachable — so nothing in the database would object to a
        // zeroed actor. What objects is `AuditableEntity.EnsureValidAuditInput`,
        // which refuses `default(UserId)` and `Guid.Empty` alike, and the constant
        // is what lets a non-request execution create an aggregate at all.
        var (sender, _) = Build(out var stores);

        await sender.Send(Command());

        stores.Added.Should().ContainSingle().Which
            .CreatedBy.Should().Be(UserId.SystemActor);
    }

    [Theory]
    [InlineData("", "hq", "a tenant slug is required")]
    [InlineData("demo-english", "", "so is the default organization's")]
    public void A_command_missing_a_required_name_is_refused_before_any_transaction(
        string tenantSlug, string organizationSlug, string because)
    {
        var command = Command() with
        {
            Slug = tenantSlug,
            DefaultOrganizationSlug = organizationSlug,
        };

        new ProvisionTenantCommandValidator().Validate(command)
            .IsValid.Should().BeFalse(because);
    }

    [Theory]
    [InlineData("Demo-English", "an uppercase letter is not URL-safe")]
    [InlineData("demo english", "nor is a space")]
    [InlineData("demo--english", "nor is a doubled hyphen")]
    [InlineData("-demo", "nor is a leading one")]
    public void A_slug_the_aggregate_would_throw_on_is_refused_first(
        string slug, string because)
    {
        // The gap this closes was measured, not imagined. ArgumentException has no entry
        // in HttpStatusMap, so a slug the factory refuses used to surface as a 500 —
        // raised inside the handler, which is after ValidationBehavior passed the
        // command, after TransactionBehavior opened a transaction, and after the tenant
        // was announced on the connection. The shape lives in one place and both layers
        // read it; this is the layer that answers the caller.
        new ProvisionTenantCommandValidator().Validate(Command() with { Slug = slug })
            .IsValid.Should().BeFalse(because);

        // And the aggregate still refuses it, so the validator is the first layer rather
        // than the only one. A test that checked only the validator would pass with the
        // factory guard deleted.
        var direct = () => TenantAggregate.Create(
            Tenant, slug, "Demo English", Clock, UserId.SystemActor);
        direct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_value_wider_than_its_column_is_refused_first()
    {
        // Same shape of defect, different guard: over the mapped width the factory
        // throws, and without a rule here that throw is a 500 raised after the
        // announcement. The two layers read one constant, so this cannot drift.
        var validator = new ProvisionTenantCommandValidator();

        validator.Validate(Command() with { Slug = new string('a', UrlSlug.MaxLength + 1) })
            .IsValid.Should().BeFalse();
        validator.Validate(Command() with
        {
            DisplayName = new string('a', MappedLength.DisplayName + 1),
        }).IsValid.Should().BeFalse();

        // The bound itself is legal — an off-by-one here would refuse a value the
        // column holds, which no test asserting only the refusal would catch.
        validator.Validate(Command() with { Slug = new string('a', UrlSlug.MaxLength) })
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void A_null_slug_is_refused_rather_than_thrown_on()
    {
        // `string` is non-nullable in the command and a deserializer is not bound by
        // that. Without Cascade(Stop) the shape predicate runs anyway and the regex
        // throws ArgumentNullException out of the validator — the same 500, moved one
        // step earlier.
        var refuse = () => new ProvisionTenantCommandValidator()
            .Validate(Command() with { Slug = null! });

        refuse.Should().NotThrow();
        refuse().IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_tenant_and_its_default_organization_may_not_share_an_id()
    {
        // The one rule neither aggregate can enforce: `Tenant.Create` never sees the
        // organization's id and `Organization.Create` never sees a reason to compare.
        // The two rows live in different tables, so a shared Guid violates nothing the
        // database checks — it simply reads, forever after, as a relationship that
        // does not exist.
        var shared = Guid.Parse("0199a000-0000-7000-8000-00000000dead");
        var command = Command() with
        {
            TenantId = TenantId.From(shared),
            DefaultOrganizationId = OrganizationId.From(shared),
        };

        new ProvisionTenantCommandValidator().Validate(command)
            .IsValid.Should().BeFalse();
    }

    [Fact]
    public void A_well_formed_command_passes()
    {
        // Every case above is a refusal, and a validator that refused everything would
        // satisfy all of them.
        new ProvisionTenantCommandValidator().Validate(Command())
            .IsValid.Should().BeTrue();
    }

    [Fact]
    public void The_validator_is_discoverable_where_the_pipeline_scans_for_it()
    {
        // ValidationBehavior resolves IValidator<T> from the container, and the
        // registration is an assembly scan with `includeInternalTypes`. A validator
        // that exists but is not found is silence: the command runs unvalidated and
        // the cross-field rule above never executes in production.
        Build().Sender.Should().NotBeNull();

        BuildProvider().GetService<IValidator<ProvisionTenantCommand>>()
            .Should().BeOfType<ProvisionTenantCommandValidator>();
    }

    private static (ISender Sender, IReadOnlyList<string> Writes) Build() =>
        Build(out _);

    private static (ISender Sender, IReadOnlyList<string> Writes) Build(
        out RecordingStores stores)
    {
        var provider = BuildProvider(out var recorded);
        stores = recorded;
        return (provider.GetRequiredService<ISender>(), recorded.Writes);
    }

    private static ServiceProvider BuildProvider() => BuildProvider(out _);

    private static ServiceProvider BuildProvider(out RecordingStores stores)
    {
        // MediatR and FluentValidation scan the same assembly the composition root
        // hands them, so a handler or validator this container cannot find is one the
        // application cannot find either.
        var applicationAssembly = typeof(ProvisionTenantCommandValidator).Assembly;
        var recording = new RecordingStores();
        stores = recording;

        var services = new ServiceCollection();
        services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssembly(applicationAssembly));
        services.AddValidatorsFromAssembly(applicationAssembly, includeInternalTypes: true);
        services.AddSingleton<ITenantWriteStore>(recording);
        services.AddSingleton<IOrganizationWriteStore>(recording);
        services.AddSingleton<IClock>(Clock);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Both write ports, recording what was written and in what order.
    /// </summary>
    /// <remarks>
    /// One object implementing two interfaces so the order across the two is a single
    /// list. Two separate fakes would each record a correct-looking sequence while the
    /// interleaving between them — the thing the real schema constrains — went
    /// unobserved.
    /// </remarks>
    private sealed class RecordingStores : ITenantWriteStore, IOrganizationWriteStore
    {
        private readonly List<string> _writes = [];

        public IReadOnlyList<string> Writes => _writes;

        public List<Organization> Added { get; } = [];

        public List<TenantAggregate> Updated { get; } = [];

        Task IAggregateWriteStore<TenantAggregate, TenantId>.AddAsync(
            TenantAggregate aggregate, CancellationToken cancellationToken)
        {
            _writes.Add("tenant:add");
            return Task.CompletedTask;
        }

        Task IAggregateWriteStore<TenantAggregate, TenantId>.UpdateAsync(
            TenantAggregate aggregate, CancellationToken cancellationToken)
        {
            _writes.Add("tenant:update");
            Updated.Add(aggregate);
            return Task.CompletedTask;
        }

        Task IAggregateWriteStore<Organization, OrganizationId>.AddAsync(
            Organization aggregate, CancellationToken cancellationToken)
        {
            _writes.Add("organization:add");
            Added.Add(aggregate);
            return Task.CompletedTask;
        }

        Task IAggregateWriteStore<Organization, OrganizationId>.UpdateAsync(
            Organization aggregate, CancellationToken cancellationToken)
        {
            _writes.Add("organization:update");
            return Task.CompletedTask;
        }
    }
}
