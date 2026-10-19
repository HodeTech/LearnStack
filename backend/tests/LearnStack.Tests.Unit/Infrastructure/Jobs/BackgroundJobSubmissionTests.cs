using System.Data.Common;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using LearnStack.Application.Messaging;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Time;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.Jobs;

public sealed class BackgroundJobSubmissionTests
{
    private static readonly TenantId Tenant = TenantId.From(Guid.Parse("019712ac-1234-7000-8000-0000000000ab"));
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-16T10:00:00Z", CultureInfo.InvariantCulture);
    private static JobModuleFixture Fixture => JobModuleFixture.Shared;

    [Theory]
    [InlineData("unenrolled")]
    [InlineData("unannounced")]
    [InlineData("readonly")]
    [InlineData("poisoned")]
    [InlineData("orphan")]
    [InlineData("untyped")]
    public async Task Refused_producer_frames_never_touch_payload_connection_clock_or_identity(string refusal)
    {
        var unit = Unit();
        var producer = new ProducerExecution(unit);
        using var invocation = refusal == "unenrolled" ? null : Enroll(producer,
            refusal == "untyped" ? null : Fixture.Request("Root"), refusal != "orphan");
        if (refusal == "unannounced") unit.GetTenantContextAnnouncement(unit.Transaction).Returns((TenantContextAnnouncement?)null);
        if (refusal == "readonly") unit.Mode.Returns(TransactionMode.ReadOnly);
        if (refusal == "poisoned") unit.IsRollbackOnly.Returns(true);
        var clock = Substitute.For<IClock>();
        var ids = Substitute.For<IGuidFactory>();
        var scheduler = new PostgresBackgroundJobScheduler(unit, producer,
            Fixture.Registry(Fixture.Transactional()), ids, clock, new BackgroundJobRuntimeOptions());
        var payload = new ForbiddenGetter();

        var action = () => scheduler.EnqueueAsync(new BackgroundJobKey<ForbiddenGetter>("jobsfixture.run.v1"), payload);
        await action.Should().ThrowAsync<InvalidOperationException>();
        payload.Reads.Should().Be(0);
        _ = unit.DidNotReceive().Connection;
        _ = clock.DidNotReceive().UtcNow;
        ids.DidNotReceive().NewUuidV7();
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("retired")]
    [InlineData("foreign")]
    [InlineData("tenant_scope")]
    [InlineData("organization_scope")]
    public async Task Key_module_and_exact_scope_refusals_happen_before_any_database_or_factory_access(string refusal)
    {
        var unit = Unit(refusal == "tenant_scope" ? OrganizationId.From(Guid.CreateVersion7()) : null);
        var producer = new ProducerExecution(unit);
        using var invocation = Enroll(producer, refusal == "foreign" ? typeof(BackgroundJobSubmissionTests) : Fixture.Request("Root"));
        var registration = Fixture.Transactional(scope: refusal == "organization_scope"
            ? BackgroundJobScope.Organization : BackgroundJobScope.TenantWide);
        var registry = BackgroundJobDefinitionRegistry.Create([registration],
            [Fixture.Ordinary("Root", registration.Scope)], [Fixture.Module], ["jobsfixture.retired.v1"]);
        var clock = Substitute.For<IClock>();
        var ids = Substitute.For<IGuidFactory>();
        var scheduler = new PostgresBackgroundJobScheduler(unit, producer, registry, ids, clock, new BackgroundJobRuntimeOptions());
        var key = refusal switch
        {
            "unknown" => "jobsfixture.future.v2",
            "retired" => "jobsfixture.retired.v1",
            _ => registration.Key,
        };

        var action = () => Submit(scheduler, key, Parameters());
        await action.Should().ThrowAsync<Exception>().Where(error => error is ArgumentException || error is InvalidOperationException);
        _ = unit.DidNotReceive().Connection;
        _ = clock.DidNotReceive().UtcNow;
        ids.DidNotReceive().NewUuidV7();
    }

    [Theory]
    [InlineData("clock")]
    [InlineData("identity")]
    public async Task A_factory_that_invalidates_the_announcement_cannot_reuse_the_captured_origin(string factory)
    {
        var unit = Unit();
        var producer = new ProducerExecution(unit);
        using var invocation = Enroll(producer, Fixture.Request("Root"));
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(_ =>
        {
            if (factory == "clock") unit.IsRollbackOnly.Returns(true);
            return Now.AddTicks(7);
        });
        var ids = Substitute.For<IGuidFactory>();
        ids.NewUuidV7().Returns(_ =>
        {
            if (factory == "identity")
                unit.GetTenantContextAnnouncement(unit.Transaction).Returns(new TenantContextAnnouncement(2, Tenant, null));
            return Guid.CreateVersion7();
        });
        var scheduler = new PostgresBackgroundJobScheduler(unit, producer,
            Fixture.Registry(Fixture.Transactional()), ids, clock, new BackgroundJobRuntimeOptions());

        var action = () => Submit(scheduler, "jobsfixture.run.v1", Parameters());
        await action.Should().ThrowAsync<InvalidOperationException>();
        _ = unit.DidNotReceive().Connection;
        if (factory == "clock") ids.DidNotReceive().NewUuidV7();
        else ids.Received(1).NewUuidV7();
    }

    [Fact]
    public async Task A_foreign_unit_cannot_borrow_an_identical_announcement()
    {
        var unit = Unit();
        var producer = new ProducerExecution(unit);
        using var invocation = Enroll(producer, Fixture.Request("Root"));
        var foreign = Unit();
        var scheduler = new PostgresBackgroundJobScheduler(foreign, producer,
            Fixture.Registry(Fixture.Transactional()), new FixedGuidFactory(), new FixedClock(Now), new BackgroundJobRuntimeOptions());
        var action = () => Submit(scheduler, "jobsfixture.run.v1", Parameters());
        await action.Should().ThrowAsync<InvalidOperationException>();
        _ = foreign.DidNotReceive().Connection;
        _ = unit.DidNotReceive().Connection;
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("uuid4")]
    public async Task A_non_v7_identity_factory_is_refused_before_connection_access(string invalid)
    {
        var unit = Unit();
        var producer = new ProducerExecution(unit);
        using var invocation = Enroll(producer, Fixture.Request("Root"));
        var ids = new FixedGuidFactory(invalid == "empty" ? Guid.Empty : Guid.NewGuid());
        var scheduler = new PostgresBackgroundJobScheduler(unit, producer,
            Fixture.Registry(Fixture.Transactional()), ids, new FixedClock(Now), new BackgroundJobRuntimeOptions());
        var action = () => Submit(scheduler, "jobsfixture.run.v1", Parameters());
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("*UUIDv7*");
        _ = unit.DidNotReceive().Connection;
    }

    [Fact]
    public async Task Interface_typed_parameters_cannot_widen_a_definition_key()
    {
        var unit = Unit();
        var producer = new ProducerExecution(unit);
        using var invocation = Enroll(producer, Fixture.Request("Root"));
        var scheduler = new PostgresBackgroundJobScheduler(unit, producer,
            Fixture.Registry(Fixture.Transactional()), new FixedGuidFactory(), new FixedClock(Now), new BackgroundJobRuntimeOptions());
        var action = () => scheduler.EnqueueAsync(new BackgroundJobKey<IBackgroundJobParameters>("jobsfixture.run.v1"),
            (IBackgroundJobParameters)Parameters());
        await action.Should().ThrowAsync<ArgumentException>();
        _ = unit.DidNotReceive().Connection;
    }

    private static IUnitOfWork Unit(OrganizationId? organization = null)
    {
        var unit = Substitute.For<IUnitOfWork>();
        unit.Transaction.Returns(Substitute.For<DbTransaction>());
        unit.HasActiveTransaction.Returns(true);
        unit.Mode.Returns(TransactionMode.ReadWrite);
        unit.GetTenantContextAnnouncement(unit.Transaction).Returns(new TenantContextAnnouncement(1, Tenant, organization));
        return unit;
    }

    // Reflection is test-only: the production authority census confines this
    // internal enrollment to the existing transaction owner. No public test seam.
    private static IDisposable Enroll(ProducerExecution producer, Type? operation, bool owner = true) =>
        (IDisposable)typeof(ProducerExecution).GetMethod("BeginInvocation", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(producer, [owner, null, null, operation, null])!;

    private static object Parameters() => Activator.CreateInstance(Fixture.Contract("Parameters"), Guid.CreateVersion7())!;

    private static async Task Submit(IBackgroundJobScheduler scheduler, string key, object parameters)
    {
        var method = typeof(IBackgroundJobScheduler).GetMethod(nameof(IBackgroundJobScheduler.EnqueueAsync))!
            .MakeGenericMethod(parameters.GetType());
        var typedKey = Activator.CreateInstance(typeof(BackgroundJobKey<>).MakeGenericType(parameters.GetType()), key)!;
        await (Task)method.Invoke(scheduler, [typedKey, parameters, CancellationToken.None])!;
    }

    public sealed record ForbiddenGetter : IBackgroundJobParameters
    {
        public int Reads { get; private set; }
        public Guid Id => ++Reads == 0 ? Guid.Empty : Guid.CreateVersion7();
    }
}
