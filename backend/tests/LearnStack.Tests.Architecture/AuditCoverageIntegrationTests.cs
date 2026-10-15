using FluentAssertions;
using LearnStack.Infrastructure.Audit;
using LearnStack.Modules.Tenancy.Application.Contracts.IntegrationEvents;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Messaging;
using MediatR;
using Xunit;

namespace LearnStack.Tests.Architecture
{
    public sealed partial class AuditCoverageTests
    {
        /// <summary>
        /// <see href="../../../docs/decisions/0057-transactional-event-production-and-completion.md">ADR-0057</see>
        /// classifies each concrete integration handler, even when its event is shared.
        /// </summary>
        [Fact]
        public void Integration_Audit_Census_Catches_An_Unregistered_Handler_For_A_Shared_Event()
        {
            var subjects = AuditSubjects(IntegrationProbeTypes());
            var catalogue = new AuditCatalog([new FirstIntegrationHandlerSource()]);

            subjects.Should().BeEquivalentTo(
                [typeof(FirstIntegrationAuditHandler), typeof(InheritedIntegrationAuditHandler), typeof(CreateOrganizationCommandProbe)],
                "the census must keep concrete handlers and MediatR requests, including inherited contracts; Fix: classify handlers separately from their events");
            UnregisteredOf(subjects, catalogue).Should().Equal([typeof(InheritedIntegrationAuditHandler)],
                "one registered consumer cannot cover another consumer of the same event; Fix: register each concrete handler");
        }

        /// <summary>
        /// <see href="../../../docs/decisions/0057-transactional-event-production-and-completion.md">ADR-0057</see>
        /// keys delivery audit classification on handler identity rather than event identity.
        /// </summary>
        [Fact]
        public void Integration_Audit_Census_Rejects_Event_Only_Classification()
        {
            var catalogue = new AuditCatalog([new EventOnlyAuditSource()]);
            var subjects = AuditSubjects([typeof(FirstIntegrationAuditHandler), typeof(InheritedIntegrationAuditHandler)]);

            UnregisteredOf(subjects, catalogue).Should().BeEquivalentTo(subjects,
                "classifying the event leaves both handlers unclassified; Fix: register concrete handler types in the owning audit source");
        }

        /// <summary>
        /// <see href="../../../docs/decisions/0057-transactional-event-production-and-completion.md">ADR-0057</see>
        /// permits explicit Off coordinators whose nested commands declare business intents.
        /// </summary>
        [Fact]
        public void Integration_Audit_Census_Accepts_Explicit_Off_For_Both_Handlers()
        {
            var catalogue = new AuditCatalog([new BothIntegrationHandlersSource()]);

            UnregisteredOf(AuditSubjects(IntegrationProbeTypes()), catalogue).Should().BeEmpty(
                "explicit Off is a real classification and nested commands own their MUST intents; Fix: distinguish Off registration from missing registration");
        }

        /// <summary>
        /// <see href="../../../docs/decisions/0044-audit-write-path.md">ADR-0044</see>
        /// and <see href="../../../docs/decisions/0057-transactional-event-production-and-completion.md">ADR-0057</see>
        /// require a matrix even when a module ships only an integration handler.
        /// </summary>
        [Fact]
        public void Integration_Only_Module_Requires_An_Audit_Matrix()
        {
            Type[] types = [typeof(LearnStack.Modules.Ghost.Application.IntegrationOnlyAuditHandler)];
            var owning = ModulesWithCode(AuditSubjects(types), types);

            owning.Should().Equal(["Ghost"],
                "a module with an integration handler is visible without an aggregate or MediatR request; Fix: use all audit subjects for module discovery");
            ModulesWithoutMatrix(owning, ModuleSpecs()).Should().Equal(["Ghost"],
                "an integration-only module cannot avoid the matrix census; Fix: add its module audit.md");
        }

        private static Type[] IntegrationProbeTypes() =>
        [
            typeof(FirstIntegrationAuditHandler), typeof(InheritedIntegrationAuditHandler),
            typeof(IInheritedIntegrationAuditContract), typeof(AbstractIntegrationAuditHandler),
            typeof(TenantCreatedIntegrationEventV1), typeof(IntegrationAuditRequestHandler),
            typeof(IntegrationAuditNotificationHandler), typeof(string),
        ];

        private interface IInheritedIntegrationAuditContract : IIntegrationEventHandler<TenantCreatedIntegrationEventV1>;

        private sealed class FirstIntegrationAuditHandler : IIntegrationEventHandler<TenantCreatedIntegrationEventV1>
        {
            internal FirstIntegrationAuditHandler() { }
            public Task HandleAsync(TenantCreatedIntegrationEventV1 @event, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private abstract class AbstractIntegrationAuditHandler : IInheritedIntegrationAuditContract
        {
            public Task HandleAsync(TenantCreatedIntegrationEventV1 @event, CancellationToken cancellationToken = default) => Task.CompletedTask;
        }

        private sealed class InheritedIntegrationAuditHandler : AbstractIntegrationAuditHandler;

        private sealed record CreateOrganizationCommandProbe : IRequest;
        private sealed class IntegrationAuditRequestHandler : IRequestHandler<CreateOrganizationCommandProbe>
        {
            public Task Handle(CreateOrganizationCommandProbe request, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed record IntegrationAuditNotification : INotification;
        private sealed class IntegrationAuditNotificationHandler : INotificationHandler<IntegrationAuditNotification>
        {
            public Task Handle(IntegrationAuditNotification notification, CancellationToken cancellationToken) => Task.CompletedTask;
        }

        private sealed class FirstIntegrationHandlerSource : IAuditCatalogSource
        {
            public string ModuleName => "customization";
            public void Describe(IAuditCatalogBuilder builder) => builder.Off<FirstIntegrationAuditHandler>().Off<CreateOrganizationCommandProbe>();
        }

        private sealed class EventOnlyAuditSource : IAuditCatalogSource
        {
            public string ModuleName => "customization";
            public void Describe(IAuditCatalogBuilder builder) => builder.Off<TenantCreatedIntegrationEventV1>();
        }

        private sealed class BothIntegrationHandlersSource : IAuditCatalogSource
        {
            public string ModuleName => "customization";
            public void Describe(IAuditCatalogBuilder builder) =>
                builder.Off<FirstIntegrationAuditHandler>().Off<InheritedIntegrationAuditHandler>().Off<CreateOrganizationCommandProbe>();
        }
    }
}

namespace LearnStack.Modules.Ghost.Application
{
    /// <summary>A handler-only module without a matrix; never part of the production census.</summary>
    internal sealed class IntegrationOnlyAuditHandler : IIntegrationEventHandler<TenantCreatedIntegrationEventV1>
    {
        public Task HandleAsync(TenantCreatedIntegrationEventV1 @event, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
