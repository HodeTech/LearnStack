using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Authorization;
using LearnStack.Application.Messaging;
using LearnStack.Application.Persistence;
using LearnStack.Infrastructure.Messaging;
using LearnStack.Modules.Customization.Application.Authorization;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Application.IntegrationEvents;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Messaging;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Tenancy;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Messaging;

/// <summary>Isolated provenance/lifetime controls; the real subscriber integration supplies provider proof.</summary>
public sealed class DeliveryOperationSourceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exact_validated_handler_binding_admits_only_its_command_and_cannot_be_installed_twice(bool taxonomy)
    {
        await using var harness = await Harness.CreateAsync(taxonomy);
        var descriptor = harness.Source.Capture(harness.RequestType, harness.Context);
        descriptor.Should().NotBeNull();
        descriptor!.Matches(harness.RequestType, harness.Context).Should().BeTrue();
        descriptor.SourceKind.Should().Be(AuthorizationSourceKind.IntegrationEvent);
        harness.Source.Capture(harness.OtherType, harness.Context).Should().BeNull();
        harness.Source.Capture(typeof(DeliveryOperationSourceTests), harness.Context).Should().BeNull();
        var repeated = () => harness.Source.Enter(harness.Prepared, harness.Subscription, harness.Envelope,
            harness.Gate, CancellationToken.None);
        repeated.Should().Throw<InvalidOperationException>();
        using var uninstalled = new DeliveryOperationSource(harness.Policies, harness.Unit, harness.Accessor);
        uninstalled.Capture(harness.RequestType, harness.Context).Should().BeNull(
            "a constructed SystemActor context and source cannot replace the validated executor's installation");
    }

    [Theory]
    [InlineData("context-reference")]
    [InlineData("actor")]
    [InlineData("origin")]
    [InlineData("tenant")]
    [InlineData("organization")]
    [InlineData("transaction")]
    [InlineData("generation")]
    [InlineData("mode")]
    [InlineData("rollback")]
    [InlineData("announcement")]
    [InlineData("cancellation")]
    [InlineData("gate")]
    [InlineData("source")]
    [InlineData("lease")]
    public async Task Retained_descriptor_refuses_each_changed_actual_owner_or_lifetime(string changed)
    {
        await using var harness = await Harness.CreateAsync(taxonomy: false);
        var descriptor = harness.Source.Capture(harness.RequestType, harness.Context);
        descriptor.Should().NotBeNull();
        descriptor!.IsActive.Should().BeTrue();
        switch (changed)
        {
            case "context-reference": harness.Accessor.Current = new MutableContext(harness.Context.TenantId); break;
            case "actor": harness.Context.Actor = UserId.From(Guid.CreateVersion7()); break;
            case "origin": harness.Context.Origin = TenantContextOrigin.HostAndClaim; break;
            case "tenant": harness.Context.TenantId = TenantId.From(Guid.CreateVersion7()); break;
            case "organization": harness.Context.OrganizationId = OrganizationId.From(Guid.CreateVersion7()); break;
            case "transaction": harness.Transaction = Substitute.For<DbTransaction>(); break;
            case "generation": harness.Announcement = harness.Announcement with { Generation = 2 }; break;
            case "mode": harness.Mode = TransactionMode.ReadOnly; break;
            case "rollback": harness.RollbackOnly = true; break;
            case "announcement": harness.Issued = false; break;
            case "cancellation": harness.Cancellation.Cancel(); break;
            case "gate": harness.Gate.Close(); break;
            case "source": harness.Source.Dispose(); break;
            case "lease": harness.Lease.Dispose(); break;
            default: throw new InvalidOperationException("Unknown isolated source control.");
        }
        descriptor.IsActive.Should().BeFalse();
        harness.Source.Capture(harness.RequestType, harness.Context).Should().BeNull();
    }

    private sealed class Harness : IAsyncDisposable
    {
        private bool _active;
        private readonly IUnitOfWorkScope _scope = Substitute.For<IUnitOfWorkScope>();
        internal IUnitOfWork Unit { get; } = Substitute.For<IUnitOfWork>();
        internal MutableContext Context { get; } = new(TenantId.From(Guid.CreateVersion7()));
        internal StaticTenantContextAccessor Accessor { get; }
        internal OperationPolicyCatalog Policies { get; } = new([new CustomizationOperationPolicySource()]);
        internal DeliveryCommitGate Gate { get; } = new();
        internal CancellationTokenSource Cancellation { get; } = new();
        internal DeliveryOperationSource Source { get; }
        internal TransactionExecutionCoordinator.PreparedDeliveryOwner Prepared { get; private set; } = null!;
        internal IntegrationEventSubscription Subscription { get; }
        internal IntegrationEventEnvelope Envelope { get; }
        internal Type RequestType { get; }
        internal Type OtherType { get; }
        internal IDisposable Lease { get; private set; } = null!;
        internal DbTransaction Transaction { get; set; } = Substitute.For<DbTransaction>();
        internal TenantContextAnnouncement Announcement { get; set; }
        internal TransactionMode Mode { get; set; } = TransactionMode.ReadWrite;
        internal bool RollbackOnly { get; set; }
        internal bool Issued { get; set; } = true;

        private Harness(bool taxonomy)
        {
            RequestType = taxonomy ? typeof(InitializeTenantLevelTaxonomyDefaultCommand) : typeof(InitializeTenantContentTypeDefaultCommand);
            OtherType = taxonomy ? typeof(InitializeTenantContentTypeDefaultCommand) : typeof(InitializeTenantLevelTaxonomyDefaultCommand);
            Accessor = new(Context);
            Announcement = new(1, Context.TenantId, null);
            Subscription = IntegrationEventHandlerRegistry.Discover(typeof(InitializeContentTypeDefaultHandler).Assembly)
                .For(typeof(TenantCreatedIntegrationEventV1)).Single(subscription => subscription.HandlerType ==
                    (taxonomy ? typeof(InitializeTaxonomyDefaultHandler) : typeof(InitializeContentTypeDefaultHandler)));
            Envelope = new(new TenantCreatedIntegrationEventV1
            {
                EventId = Guid.CreateVersion7(),
                TenantId = Context.TenantId.Value,
                OccurredAt = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero)
            },
                "00-11111111111111111111111111111111-1111111111111111-01");
            Unit.HasActiveTransaction.Returns(_ => _active);
            Unit.IsRollbackOnly.Returns(_ => RollbackOnly);
            Unit.Mode.Returns(_ => _active ? Mode : null);
            Unit.Transaction.Returns(_ => _active ? Transaction : null);
            Unit.GetTenantContextAnnouncement(Arg.Any<DbTransaction?>()).Returns(_ => _active ? Announcement : null);
            Unit.IsTenantContextIssuedOn(Arg.Any<DbTransaction?>()).Returns(_ => Issued);
            _scope.IsOwner.Returns(true);
            Unit.BeginTransactionAsync(TransactionMode.ReadWrite, Arg.Any<CancellationToken>()).Returns(_ =>
            {
                _active = true;
                return Task.FromResult(_scope);
            });
#pragma warning disable CA2012 // Configures a substitute, not a provider disposal.
            _scope.DisposeAsync().Returns(_ => { _active = false; return ValueTask.CompletedTask; });
#pragma warning restore CA2012
            Source = new(Policies, Unit, Accessor);
        }

        internal static async Task<Harness> CreateAsync(bool taxonomy)
        {
            var harness = new Harness(taxonomy);
            harness.Prepared = await TransactionExecutionCoordinator.PrepareDeliveryOwnerAsync(harness.Unit, harness.Context);
            harness.Lease = harness.Source.Enter(harness.Prepared, harness.Subscription, harness.Envelope, harness.Gate, harness.Cancellation.Token);
            return harness;
        }
        public async ValueTask DisposeAsync()
        {
            Lease.Dispose();
            Source.Dispose();
            await Prepared.DisposeAsync();
            Cancellation.Dispose();
        }
    }

    private sealed class MutableContext(TenantId tenant) : ITenantContext
    {
        public bool IsResolved => true;
        public TenantId TenantId { get; set; } = tenant;
        public OrganizationId? OrganizationId { get; set; }
        public UserId? Actor { get; set; } = LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public UserId? UserId => Actor;
        public TenantContextOrigin? Origin { get; set; } = TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => "customization";
    }
}
