using FluentAssertions;
using FluentValidation;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.SharedKernel.Validation;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Modules.Customization;

/// <summary>
/// What the customization write path writes, in what order, and what it refuses
/// before a transaction is opened.
/// </summary>
/// <remarks>
/// <para>
/// Driven through <c>ISender</c> resolved from a container rather than by
/// constructing the handlers, for the reason
/// <c>ProvisionTenantCommandTests</c> gives: the handlers are <c>internal</c>, and
/// discovery by assembly scan is itself part of what needs proving — a handler
/// that compiles but is not discoverable is a 500 at the first call.
/// </para>
/// <para>
/// The database half — that the writes and the generation bump commit together as
/// <c>learnstack_app</c>, and that the partial index refuses a second live
/// revision — cannot be made against fakes and lives in the integration suite.
/// </para>
/// </remarks>
public sealed class CustomizationCommandTests
{
    private static readonly FixedClock Clock = new(
        new DateTimeOffset(2026, 9, 6, 9, 0, 0, TimeSpan.Zero));

    private static readonly TenantId Tenant =
        TenantId.From(Guid.Parse("0199a000-0000-7000-8000-000000000001"));

    private static readonly Guid ContentTypeId =
        Guid.Parse("0199a000-0000-7000-8000-0000000000c1");

    private static readonly Guid TaxonomyId =
        Guid.Parse("0199a000-0000-7000-8000-0000000000d1");

    private const string Schema =
        """{"$schema":"https://json-schema.org/draft/2020-12/schema","type":"object","properties":{"body":{"type":"string"}}}""";

    private static readonly Dictionary<string, string> Name =
        new(StringComparer.Ordinal) { ["en"] = "Announcement" };

    private static RegisterTenantContentTypeCommand RegisterContentType(
        Guid? id = null, string key = "announcement", int version = 1) =>
        new(id ?? ContentTypeId, key, version, Name, Schema, "default-card");

    private static RegisterTenantLevelTaxonomyCommand RegisterTaxonomy(
        Guid? id = null, string key = "proficiency", int version = 1,
        IReadOnlyList<TaxonomyItemInput>? items = null) =>
        new(id ?? TaxonomyId, key, version, Name,
            items ?? [new TaxonomyItemInput("beginner", Name, 0)]);

    // ── The write path ────────────────────────────────────────────────────

    [Fact]
    public async Task Registering_a_content_type_writes_a_draft_and_bumps_the_generation()
    {
        // The bump is half the operation, not an afterthought: a content type written
        // without one is invisible to every reader still holding a key composed
        // against the previous generation.
        var (sender, stores) = Build();

        var result = await sender.Send(RegisterContentType());

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(nameof(CustomizationStatus.Draft));
        stores.Writes.Should().Equal("content-type:add", "generation:bump");
    }

    [Fact]
    public async Task Registering_a_taxonomy_carries_its_bands_in_the_same_write()
    {
        var (sender, stores) = Build();

        var result = await sender.Send(RegisterTaxonomy(items:
        [
            new TaxonomyItemInput("a1", Name, 0),
            new TaxonomyItemInput("a2", Name, 1),
        ]));

        result.IsSuccess.Should().BeTrue();
        result.Value!.ItemCount.Should().Be(2);
        stores.Writes.Should().Equal("taxonomy:add", "generation:bump");

        // The bands are on the aggregate the store received, not merely counted in
        // the response — a DTO computed from the request would report two either way.
        stores.AddedTaxonomies.Single().Items.Select(item => item.Key)
            .Should().Equal("a1", "a2");
    }

    [Fact]
    public async Task Publishing_deprecates_the_incumbent_before_it_publishes_the_successor()
    {
        // The order is the whole case. The partial index admits one live revision per
        // key, so a publish that ran first would be refused by PostgreSQL after the
        // successor's UPDATE had already gone out — an ordinary succession turned
        // into a constraint violation the caller cannot act on.
        var (sender, stores) = Build();

        await sender.Send(RegisterContentType(version: 1));
        await sender.Send(new PublishTenantContentTypeCommand(ContentTypeId));

        var successorId = Guid.Parse("0199a000-0000-7000-8000-0000000000c2");
        await sender.Send(RegisterContentType(successorId, version: 2));

        stores.Writes.Clear();
        stores.UpdatedStatuses.Clear();
        var result = await sender.Send(new PublishTenantContentTypeCommand(successorId));

        result.IsSuccess.Should().BeTrue();
        result.Value!.Status.Should().Be(nameof(CustomizationStatus.Active));
        stores.Writes.Should().Equal(
            "content-type:update", "content-type:update", "generation:bump");

        stores.ContentTypes[ContentTypeId].Status
            .Should().Be(CustomizationStatus.Deprecated, "the incumbent is retired");

        // Both updates are spelled the same in Writes, so the order that matters is
        // only visible in what each one carried.
        stores.UpdatedStatuses.Should().Equal(
            CustomizationStatus.Deprecated, CustomizationStatus.Active);
    }

    [Fact]
    public async Task Publishing_the_first_revision_of_a_key_deprecates_nothing()
    {
        var (sender, stores) = Build();

        await sender.Send(RegisterContentType());
        stores.Writes.Clear();

        var result = await sender.Send(new PublishTenantContentTypeCommand(ContentTypeId));

        result.IsSuccess.Should().BeTrue();
        stores.Writes.Should().Equal("content-type:update", "generation:bump");
    }

    [Fact]
    public async Task Publishing_a_taxonomy_refuses_one_with_no_bands()
    {
        // The aggregate's EnsurePublishable states the same rule and throws, which is
        // a 500. A vocabulary with no bands is something a tenant admin fixes.
        var (sender, stores) = Build();
        var empty = TenantLevelTaxonomy.Create(
            TenantLevelTaxonomyId.From(TaxonomyId), Tenant, "proficiency", 1,
            LocalizedText.From(Name), Clock, UserId.SystemActor);
        stores.Taxonomies[TaxonomyId] = empty;

        var result = await sender.Send(new PublishTenantLevelTaxonomyCommand(TaxonomyId));

        result.IsFailure.Should().BeTrue();
        result.Error!.Details!["TaxonomyId"].Single().Key
            .Should().Be("lockey_taxonomy_items_required");
    }

    [Fact]
    public async Task Publishing_something_already_live_is_a_refusal_and_not_an_exception()
    {
        var (sender, _) = Build();

        await sender.Send(RegisterContentType());
        await sender.Send(new PublishTenantContentTypeCommand(ContentTypeId));

        var result = await sender.Send(new PublishTenantContentTypeCommand(ContentTypeId));

        result.IsFailure.Should().BeTrue();
        result.Error!.Details!["ContentTypeId"].Single().Key
            .Should().Be("lockey_customization_not_a_draft");
    }

    [Fact]
    public async Task Publishing_an_id_this_tenant_does_not_have_is_a_refusal()
    {
        // "Not this tenant's" and "does not exist" are one answer by construction:
        // the query filter and the policy both drop another tenant's row, so there is
        // no lookup that could tell them apart — and none that should.
        var (sender, _) = Build();

        var result = await sender.Send(new PublishTenantContentTypeCommand(ContentTypeId));

        result.IsFailure.Should().BeTrue();
        result.Error!.Details!["ContentTypeId"].Single().Key
            .Should().Be("lockey_customization_not_found");
    }

    [Fact]
    public async Task A_document_the_gate_refuses_is_never_written_and_never_bumps()
    {
        var (sender, stores) = Build(admitSchemas: false);

        var result = await sender.Send(RegisterContentType());

        result.IsFailure.Should().BeTrue();
        result.Error!.Message.Key.Should().Be("lockey_validation_failed");
        stores.Writes.Should().BeEmpty(
            "a refused document leaves no row and no generation behind");
    }

    [Fact]
    public async Task The_gate_s_own_pointer_survives_to_the_caller()
    {
        // ADR-0043 orders the gates so the ones that can name a location run first.
        // Rebuilding the refusal under a field name would throw that location away.
        var (sender, _) = Build(admitSchemas: false);

        var result = await sender.Send(RegisterContentType());

        result.Error!.Details.Should().ContainKey("/properties/body");
    }

    [Fact]
    public async Task A_uniqueness_collision_names_which_uniqueness_it_was()
    {
        var (sender, stores) = Build();
        stores.NextConflict = "ux_tenant_content_types_tenant_id_key_active";

        var result = await sender.Send(RegisterContentType());

        result.IsFailure.Should().BeTrue();
        result.Error!.Details!["Key"].Single().Key
            .Should().Be("lockey_customization_key_already_live");
        stores.Writes.Should().NotContain(
            "generation:bump", "a write the database refused invalidates nothing");
    }

    [Fact]
    public async Task An_unresolved_context_writes_nothing()
    {
        var (sender, stores) = Build(resolved: false);

        var result = await sender.Send(RegisterContentType());

        result.IsFailure.Should().BeTrue();
        result.Error!.Message.Key.Should().Be("lockey_tenant_mismatch");
        stores.Writes.Should().BeEmpty();
    }

    // ── What the validators refuse ────────────────────────────────────────
    //
    // Asserted through the validator resolved from the container, not through the
    // handler: the pipeline behavior that runs it is composed once at the root and
    // has its own cases, and driving refusals through a handler here would test
    // that wiring twice while proving nothing about the rules. `Discoverable` below
    // is what ties the two together — a validator the scan cannot find is silence,
    // and the command then runs unvalidated in production.

    [Theory]
    [InlineData("", "lockey_customization_key_required")]
    [InlineData("Announcement", "lockey_customization_key_not_url_safe")]
    [InlineData("has space", "lockey_customization_key_not_url_safe")]
    public void A_key_that_is_not_a_slug_is_refused(string key, string expected)
    {
        Refuse(RegisterContentType(key: key)).Should().Be(expected);

        // And the aggregate still refuses it, so the validator is the first layer
        // rather than the only one. A case that checked only the validator would pass
        // with the factory guard deleted.
        var create = () => CustomizationKey.EnsureValid(key, nameof(key));
        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void A_key_past_the_customization_cap_is_refused()
    {
        Refuse(RegisterContentType(key: new string('a', CustomizationKey.MaxLength + 1)))
            .Should().Be("lockey_customization_key_too_long");
    }

    [Fact]
    public void A_key_at_the_customization_cap_is_accepted()
    {
        // The pair, one step apart: a cap asserted only from the failing side passes
        // with the constant off by one in the permissive direction.
        Refuse(RegisterContentType(key: new string('a', CustomizationKey.MaxLength)))
            .Should().BeNull();
    }

    [Fact]
    public void An_unassigned_identifier_is_refused()
    {
        Refuse(RegisterContentType(id: Guid.Empty)).Should().Be("lockey_identifier_required");
    }

    [Fact]
    public void A_renderer_key_outside_the_closed_set_is_refused()
    {
        Refuse(new RegisterTenantContentTypeCommand(
                ContentTypeId, "announcement", 1, Name, Schema, "carousel"))
            .Should().Be("lockey_renderer_key_unknown");
    }

    [Fact]
    public void Every_renderer_key_the_closed_set_names_is_accepted()
    {
        // The other side of the same rule. Asserted over the set rather than over one
        // member, because a predicate that answered false for everything would pass
        // the refusal case on its own.
        foreach (var key in CompositeRendererKey.All)
        {
            Refuse(new RegisterTenantContentTypeCommand(
                    ContentTypeId, "announcement", 1, Name, Schema, key))
                .Should().BeNull(key);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_schema_version_below_one_is_refused(int version)
    {
        Refuse(RegisterContentType(version: version))
            .Should().Be("lockey_schema_version_invalid");
    }

    [Fact]
    public void A_display_name_the_aggregate_would_refuse_is_refused_here_instead()
    {
        // The validator runs the factory rather than restating its six rules, so
        // "what this refuses" and "what the aggregate refuses" are one set. Without
        // it a malformed tag is an ArgumentException, which has no entry in
        // HttpStatusMap and answers 500 for something the author can fix.
        var malformed = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["not a tag"] = "x",
        };

        Refuse(new RegisterTenantContentTypeCommand(
                ContentTypeId, "announcement", 1, malformed, Schema, "default-card"))
            .Should().Be("lockey_display_name_not_localizable");

        var build = () => LocalizedText.From(malformed);
        build.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_empty_display_name_is_refused()
    {
        Refuse(new RegisterTenantContentTypeCommand(
                ContentTypeId, "announcement", 1,
                new Dictionary<string, string>(StringComparer.Ordinal), Schema, "default-card"))
            .Should().Be("lockey_display_name_not_localizable");
    }

    [Fact]
    public void A_taxonomy_with_no_bands_is_refused()
    {
        RefuseTaxonomy(RegisterTaxonomy(items: []))
            .Should().Be("lockey_taxonomy_items_required");
    }

    [Fact]
    public void More_bands_than_the_aggregate_holds_are_refused()
    {
        var tooMany = Enumerable.Range(0, TenantLevelTaxonomy.MaxItems + 1)
            .Select(index => new TaxonomyItemInput($"band-{index}", Name, (short)index))
            .ToList();

        RefuseTaxonomy(RegisterTaxonomy(items: tooMany))
            .Should().Be("lockey_taxonomy_items_too_many");
    }

    [Fact]
    public void Exactly_as_many_bands_as_the_aggregate_holds_are_accepted()
    {
        var atCap = Enumerable.Range(0, TenantLevelTaxonomy.MaxItems)
            .Select(index => new TaxonomyItemInput($"band-{index}", Name, (short)index))
            .ToList();

        RefuseTaxonomy(RegisterTaxonomy(items: atCap)).Should().BeNull();
    }

    [Fact]
    public void A_band_key_that_is_not_a_slug_is_refused()
    {
        RefuseTaxonomy(RegisterTaxonomy(items: [new TaxonomyItemInput("A1", Name, 0)]))
            .Should().Be("lockey_customization_key_not_url_safe");
    }

    [Fact]
    public void Band_metadata_that_is_not_json_is_refused()
    {
        RefuseTaxonomy(RegisterTaxonomy(items:
                [new TaxonomyItemInput("a1", Name, 0, "not json")]))
            .Should().Be("lockey_taxonomy_item_metadata_not_json");
    }

    [Fact]
    public void Band_metadata_that_is_absent_is_accepted()
    {
        // Null is the absence of metadata, not a malformed value — the column is
        // nullable and a band without it is ordinary.
        RefuseTaxonomy(RegisterTaxonomy(items: [new TaxonomyItemInput("a1", Name, 0)]))
            .Should().BeNull();
    }

    [Fact]
    public void Every_command_has_a_validator_the_scan_can_find()
    {
        // A validator that exists but is not found is silence: the command runs
        // unvalidated and every rule above never executes in production. The scan
        // passes includeInternalTypes, and the validators are internal for the same
        // reason the handlers are.
        var provider = Provider();

        provider.GetService<IValidator<RegisterTenantContentTypeCommand>>().Should().NotBeNull();
        provider.GetService<IValidator<PublishTenantContentTypeCommand>>().Should().NotBeNull();
        provider.GetService<IValidator<RegisterTenantLevelTaxonomyCommand>>().Should().NotBeNull();
        provider.GetService<IValidator<PublishTenantLevelTaxonomyCommand>>().Should().NotBeNull();
    }

    /// <summary>The single error code one refused command produces, or nothing.</summary>
    /// <remarks>
    /// Single, deliberately. A rule that fired alongside another would make an
    /// assertion on "contains" pass while the message a caller reads was the wrong
    /// one, and <c>Cascade(Stop)</c> on every rule is what makes one the right count.
    /// </remarks>
    private static string? Refuse(RegisterTenantContentTypeCommand command) =>
        Single(Provider().GetRequiredService<IValidator<RegisterTenantContentTypeCommand>>()
            .Validate(command));

    private static string? RefuseTaxonomy(RegisterTenantLevelTaxonomyCommand command) =>
        Single(Provider().GetRequiredService<IValidator<RegisterTenantLevelTaxonomyCommand>>()
            .Validate(command));

    private static string? Single(FluentValidation.Results.ValidationResult result)
    {
        if (result.IsValid)
        {
            return null;
        }

        result.Errors.Select(failure => failure.ErrorCode).Distinct(StringComparer.Ordinal)
            .Should().ContainSingle("one refusal names one thing to fix");

        return result.Errors[0].ErrorCode;
    }

    // ── Wiring ────────────────────────────────────────────────────────────

    private static (ISender Sender, RecordingStores Stores) Build(
        bool resolved = true, bool admitSchemas = true)
    {
        var stores = new RecordingStores();
        return (Provider(stores, resolved, admitSchemas).GetRequiredService<ISender>(), stores);
    }

    private static ServiceProvider Provider(
        RecordingStores? stores = null, bool resolved = true, bool admitSchemas = true)
    {
        // MediatR and FluentValidation scan the same assembly the composition root
        // hands them, so a handler or validator this container cannot find is one the
        // application cannot find either.
        var applicationAssembly = typeof(ITenantContentTypeStore).Assembly;
        stores ??= new RecordingStores();

        var services = new ServiceCollection();
        services.AddMediatR(configuration =>
            configuration.RegisterServicesFromAssembly(applicationAssembly));
        services.AddValidatorsFromAssembly(applicationAssembly, includeInternalTypes: true);
        services.AddSingleton<ITenantContentTypeStore>(stores);
        services.AddSingleton<ITenantLevelTaxonomyStore>(stores);
        services.AddSingleton<ICustomizationGenerationStore>(stores);
        services.AddSingleton<IJsonSchemaValidator>(new StubGate(admitSchemas));
        services.AddSingleton<IClock>(Clock);
        services.AddSingleton<ITenantContext>(
            resolved ? new ResolvedContext(Tenant) : new UnresolvedTenantContext());

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Every port a customization handler takes, recording what was written and in
    /// what order.
    /// </summary>
    /// <remarks>
    /// One object behind all three, so the interleaving between an aggregate write
    /// and the generation bump is a single list. Three separate fakes would each
    /// record a correct-looking sequence while the order between them — the thing
    /// the transaction constrains — went unobserved.
    /// </remarks>
    private sealed class RecordingStores
        : ITenantContentTypeStore, ITenantLevelTaxonomyStore, ICustomizationGenerationStore
    {
        private long _generation;

        public List<string> Writes { get; } = [];

        public Dictionary<Guid, TenantContentType> ContentTypes { get; } = [];

        public Dictionary<Guid, TenantLevelTaxonomy> Taxonomies { get; } = [];

        public List<TenantLevelTaxonomy> AddedTaxonomies { get; } = [];

        /// <summary>The constraint the next write collides with, or nothing.</summary>
        public string? NextConflict { get; set; }

        /// <summary>
        /// The status each update carried, in the order the store received them.
        /// </summary>
        /// <remarks>
        /// The order between two updates of one aggregate type is what the partial
        /// index depends on, and it is invisible in a list of method names — both
        /// updates are spelled the same.
        /// </remarks>
        public List<CustomizationStatus> UpdatedStatuses { get; } = [];

        Task IAggregateWriteStore<TenantContentType, TenantContentTypeId>.AddAsync(
            TenantContentType aggregate, CancellationToken cancellationToken)
        {
            Conflict();
            Writes.Add("content-type:add");
            ContentTypes[aggregate.Id.Value] = aggregate;
            return Task.CompletedTask;
        }

        Task IAggregateWriteStore<TenantContentType, TenantContentTypeId>.UpdateAsync(
            TenantContentType aggregate, CancellationToken cancellationToken)
        {
            Conflict();
            Writes.Add("content-type:update");
            UpdatedStatuses.Add(aggregate.Status);
            return Task.CompletedTask;
        }

        public Task<TenantContentType?> FindAsync(
            TenantContentTypeId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(ContentTypes.GetValueOrDefault(id.Value));

        Task<TenantContentType?> ITenantContentTypeStore.FindActiveAsync(
            string key, CancellationToken cancellationToken) =>
            Task.FromResult(ContentTypes.Values.SingleOrDefault(
                contentType => contentType.Key == key
                    && contentType.Status == CustomizationStatus.Active
                    && contentType.DeletedAt is null));

        Task IAggregateWriteStore<TenantLevelTaxonomy, TenantLevelTaxonomyId>.AddAsync(
            TenantLevelTaxonomy aggregate, CancellationToken cancellationToken)
        {
            Conflict();
            Writes.Add("taxonomy:add");
            Taxonomies[aggregate.Id.Value] = aggregate;
            AddedTaxonomies.Add(aggregate);
            return Task.CompletedTask;
        }

        Task IAggregateWriteStore<TenantLevelTaxonomy, TenantLevelTaxonomyId>.UpdateAsync(
            TenantLevelTaxonomy aggregate, CancellationToken cancellationToken)
        {
            Conflict();
            Writes.Add("taxonomy:update");
            UpdatedStatuses.Add(aggregate.Status);
            return Task.CompletedTask;
        }

        public Task<TenantLevelTaxonomy?> FindAsync(
            TenantLevelTaxonomyId id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Taxonomies.GetValueOrDefault(id.Value));

        Task<TenantLevelTaxonomy?> ITenantLevelTaxonomyStore.FindActiveAsync(
            string key, CancellationToken cancellationToken) =>
            Task.FromResult(Taxonomies.Values.SingleOrDefault(
                taxonomy => taxonomy.Key == key
                    && taxonomy.Status == CustomizationStatus.Active
                    && taxonomy.DeletedAt is null));

        public Task<long> BumpAsync(TenantId tenantId, CancellationToken cancellationToken = default)
        {
            Writes.Add("generation:bump");
            return Task.FromResult(++_generation);
        }

        private void Conflict()
        {
            if (NextConflict is not { } constraint)
            {
                return;
            }

            NextConflict = null;
            throw new AggregateConflictException("duplicate key value", constraint);
        }
    }

    /// <summary>The gate, decided by the test rather than by a real document.</summary>
    /// <remarks>
    /// The four gates themselves are measured against the real library in
    /// <c>JsonSchemaNetValidatorTests</c>; what these cases need is the handler's
    /// behaviour on each answer, and a document contrived to fail a particular gate
    /// would pin the library's message rather than the handler's response to it.
    /// </remarks>
    private sealed class StubGate(bool admits) : IJsonSchemaValidator
    {
        public Result<None> AdmitSchema(string jsonSchema) =>
            admits
                ? Result.Ok(None.Value)
                : Result.Fail<None>(new Error(
                    new LocalizedMessage("lockey_validation_failed"),
                    new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                    {
                        ["/properties/body"] = [new LocalizedMessage("lockey_validation_failed")],
                    }));

        public Result<None> ValidateInstance(string admittedSchema, string instanceJson) =>
            Result.Ok(None.Value);
    }

    private sealed class ResolvedContext(TenantId tenantId) : ITenantContext
    {
        public bool IsResolved => true;

        public TenantContextOrigin? Origin => TenantContextOrigin.Ambient;

        public TenantId TenantId => tenantId;

        public OrganizationId? OrganizationId => null;

        public UserId? UserId => null;

        public string? CorrelationId => null;

        public string? ModuleName => "customization";
    }
}
