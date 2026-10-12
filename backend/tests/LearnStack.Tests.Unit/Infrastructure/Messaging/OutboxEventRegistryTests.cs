using FluentAssertions;
using LearnStack.Infrastructure.Messaging;
using LearnStack.SharedKernel.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Messaging;

public sealed class OutboxEventRegistryTests
{
    private const string ContentTypeSubscription = "customization.initialize-content-type-default.v1";
    private const string TaxonomySubscription = "customization.initialize-taxonomy-default.v1";
    private const string EarlierIdentity = "LearnStack.Previous.TenantCreatedIntegrationEventV1, LearnStack.Previous.Contracts";

    [Fact]
    public void Pending_holds_the_canonical_identity_and_every_alias_without_constructing_handlers()
    {
        ThrowingHandler.Constructions = 0;
        var handlers = Handlers(services =>
            services.AddScoped<IIntegrationEventHandler<OtherEvent>, ThrowingHandler>());

        // A separate Active declaration covers the discovered handler. Merely building
        // the manifest must not resolve it, including on the Pending lookup path.
        var registry = Create(
        [
            OutboxEventRegistration.Required(typeof(SampleEvent),
            [
                OutboxSubscriptionManifest.Pending(ContentTypeSubscription),
                OutboxSubscriptionManifest.Pending(TaxonomySubscription),
            ], EarlierIdentity),
            OutboxEventRegistration.Required(typeof(OtherEvent),
            [OutboxSubscriptionManifest.Active("test.other.v1", typeof(ThrowingHandler))]),
        ], handlers: handlers);

        var canonical = typeof(SampleEvent).AssemblyQualifiedName!;
        registry.PendingIdentities.Should().BeEquivalentTo([canonical, EarlierIdentity]);
        registry.Resolve(canonical).Disposition.Should().Be(OutboxEventDisposition.Pending);
        registry.Resolve(EarlierIdentity).EventType.Should().Be<SampleEvent>();
        registry.Resolve(EarlierIdentity).Subscriptions.Select(subscription => subscription.Id)
            .Should().BeEquivalentTo([ContentTypeSubscription, TaxonomySubscription]);
        ThrowingHandler.Constructions.Should().Be(0);
    }

    [Fact]
    public void Active_and_intentional_absence_have_distinct_exact_outcomes()
    {
        var registry = Create(
        [
            OutboxEventRegistration.Required(typeof(SampleEvent),
                [OutboxSubscriptionManifest.Active("test.sample.v1", typeof(SampleHandler))]),
            OutboxEventRegistration.IntentionallyUnsubscribed(typeof(OtherEvent), EarlierIdentity),
        ],
        handlers: Handlers(services =>
            services.AddScoped<IIntegrationEventHandler<SampleEvent>, SampleHandler>()));

        registry.Resolve(typeof(SampleEvent).AssemblyQualifiedName).Disposition
            .Should().Be(OutboxEventDisposition.Active);
        registry.Resolve(EarlierIdentity).Disposition
            .Should().Be(OutboxEventDisposition.IntentionallyUnsubscribed);
        registry.PendingIdentities.Should().BeEmpty();
        registry.Resolve(typeof(OtherEvent).AssemblyQualifiedName).Subscriptions.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_and_retired_names_never_resolve_a_type_from_stored_text()
    {
        var registry = Create(
            [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(SampleEvent))],
            retired: [EarlierIdentity]);

        var retired = registry.Resolve(EarlierIdentity);
        retired.Disposition.Should().Be(OutboxEventDisposition.Retired);
        retired.EventType.Should().BeNull();
        retired.EventTypeLabel.Should().Be("retired");

        // This is a loadable CLR name in the test assembly, but it was never declared.
        // Resolving it would make a database row an assembly-loading instruction.
        var undeclared = registry.Resolve(typeof(OtherEvent).AssemblyQualifiedName);
        undeclared.Disposition.Should().Be(OutboxEventDisposition.Unknown);
        undeclared.EventType.Should().BeNull();
        undeclared.EventTypeLabel.Should().Be("unknown");
    }

    [Theory]
    [InlineData("test.sample.v0")]
    [InlineData("test.sample.v01")]
    [InlineData("Test.sample.v1")]
    [InlineData("test.Sample.v1")]
    [InlineData("test.sample.v1 ")]
    [InlineData("test..v1")]
    [InlineData("test.sample.v1.extra")]
    [InlineData("test-scope.sample.v1")]
    public void Subscription_ids_follow_the_closed_stable_grammar(string id)
    {
        var build = () => Create(
            [OutboxEventRegistration.Required(typeof(SampleEvent),
                [OutboxSubscriptionManifest.Pending(id)])]);

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Alias_and_retired_collisions_refuse_composition()
    {
        var canonical = typeof(SampleEvent).AssemblyQualifiedName!;
        Action duplicateAlias = () => Create(
            [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(SampleEvent),
                EarlierIdentity, EarlierIdentity)]);
        Action canonicalAlias = () => Create(
            [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(SampleEvent), canonical)]);
        Action retiredAlias = () => Create(
            [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(SampleEvent), EarlierIdentity)],
            retired: [EarlierIdentity]);
        Action duplicateEvent = () => Create(
        [
            OutboxEventRegistration.IntentionallyUnsubscribed(typeof(SampleEvent)),
            OutboxEventRegistration.IntentionallyUnsubscribed(typeof(SampleEvent)),
        ]);

        duplicateAlias.Should().Throw<InvalidOperationException>();
        canonicalAlias.Should().Throw<InvalidOperationException>();
        retiredAlias.Should().Throw<InvalidOperationException>();
        duplicateEvent.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void A_subscription_id_cannot_name_two_independent_consumers()
    {
        var build = () => Create(
        [
            OutboxEventRegistration.Required(typeof(SampleEvent),
                [OutboxSubscriptionManifest.Pending(ContentTypeSubscription)]),
            OutboxEventRegistration.Required(typeof(OtherEvent),
                [OutboxSubscriptionManifest.Pending(ContentTypeSubscription)]),
        ]);

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Active_manifest_and_discovered_handlers_must_match_in_both_directions()
    {
        Action missingHandler = () => Create(
            [OutboxEventRegistration.Required(typeof(SampleEvent),
                [OutboxSubscriptionManifest.Active("test.sample.v1", typeof(SampleHandler))])]);
        Action undeclaredHandler = () => Create(
            [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(SampleEvent))],
            handlers: Handlers(services =>
                services.AddScoped<IIntegrationEventHandler<SampleEvent>, SampleHandler>()));
        Action pendingHandler = () => Create(
            [OutboxEventRegistration.Required(typeof(SampleEvent),
                [OutboxSubscriptionManifest.Pending(ContentTypeSubscription)])],
            handlers: Handlers(services =>
                services.AddScoped<IIntegrationEventHandler<SampleEvent>, SampleHandler>()));

        missingHandler.Should().Throw<InvalidOperationException>();
        undeclaredHandler.Should().Throw<InvalidOperationException>();
        pendingHandler.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Active_handlers_must_be_closed_constructible_types()
    {
        var open = typeof(GenericHandler<>);
        var discovered = IntegrationEventHandlerRegistry.Discover(typeof(SampleEvent).Assembly);
        discovered.All.Should().Contain(subscription =>
            subscription.EventType == typeof(SampleEvent) && subscription.HandlerType == open,
            "the discovery census alone does not reject an open handler");

        var openHandlers = IntegrationEventHandlerRegistry.FromServiceDescriptors(
            [ServiceDescriptor.Scoped(typeof(IIntegrationEventHandler<SampleEvent>), open)]);
        var build = () => Create(
            [OutboxEventRegistration.Required(typeof(SampleEvent),
                [OutboxSubscriptionManifest.Active("test.sample.v1", open)])],
            handlers: openHandlers);
        build.Should().Throw<InvalidOperationException>().WithMessage("*concrete handler*");

        var closed = typeof(GenericHandler<int>);
        var closedHandlers = IntegrationEventHandlerRegistry.FromServiceDescriptors(
            [ServiceDescriptor.Scoped(typeof(IIntegrationEventHandler<SampleEvent>), closed)]);
        var control = Create(
            [OutboxEventRegistration.Required(typeof(SampleEvent),
                [OutboxSubscriptionManifest.Active("test.sample.v1", closed)])],
            handlers: closedHandlers);
        control.Resolve(typeof(SampleEvent).AssemblyQualifiedName).Disposition
            .Should().Be(OutboxEventDisposition.Active);
    }

    [Fact]
    public void A_contract_outside_the_explicit_assembly_allowlist_is_rejected()
    {
        var build = () => OutboxEventRegistry.Create(
            [OutboxEventRegistration.IntentionallyUnsubscribed(typeof(SampleEvent))],
            [], Handlers(), [typeof(IntegrationEventBase).Assembly]);

        build.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void An_empty_contract_census_cannot_make_every_row_unknown()
    {
        var build = () => Create([]);

        build.Should().Throw<InvalidOperationException>();
    }

    private static OutboxEventRegistry Create(
        IEnumerable<OutboxEventRegistration> registrations,
        IEnumerable<string>? retired = null,
        IntegrationEventHandlerRegistry? handlers = null) =>
        OutboxEventRegistry.Create(
            registrations,
            retired ?? [],
            handlers ?? Handlers(),
            [typeof(SampleEvent).Assembly]);

    private static IntegrationEventHandlerRegistry Handlers(
        Action<ServiceCollection>? register = null)
    {
        var services = new ServiceCollection();
        register?.Invoke(services);
        return IntegrationEventHandlerRegistry.FromServiceDescriptors(services);
    }

    private sealed record SampleEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.test.sample";
        public override string PartitionKey => TenantId.ToString("D");
    }

    private sealed record OtherEvent : IntegrationEventBase
    {
        public override string Topic => "learnstack.test.other";
        public override string PartitionKey => TenantId.ToString("D");
    }

    private sealed class SampleHandler : IIntegrationEventHandler<SampleEvent>
    {
        public Task HandleAsync(SampleEvent integrationEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class GenericHandler<T> : IIntegrationEventHandler<SampleEvent>
    {
        public Task HandleAsync(SampleEvent integrationEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingHandler : IIntegrationEventHandler<OtherEvent>
    {
        public static int Constructions;

        public ThrowingHandler()
        {
            Constructions++;
            throw new InvalidOperationException("Handler construction was reached.");
        }

        public Task HandleAsync(OtherEvent integrationEvent, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
