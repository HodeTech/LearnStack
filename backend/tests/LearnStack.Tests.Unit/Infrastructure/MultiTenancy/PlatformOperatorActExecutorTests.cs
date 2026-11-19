using System.Diagnostics.Metrics;
using FluentAssertions;
using LearnStack.Application.Authorization;
using LearnStack.Infrastructure.PlatformOperator;
using LearnStack.Modules.Identity.Application;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Platform;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.Tests.Fixtures;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Infrastructure.MultiTenancy;

/// <summary>
/// What the operator act executor and its source decide before a handler runs (ADR-0102 § 1): which
/// requests and admissions it accepts, what context and provenance the act runs under, and that
/// both are gone when the act is.
/// </summary>
/// <remarks>
/// No database and no pipeline: the sender is a double that observes the ambient context and asks the
/// scoped source for a descriptor while the act is in flight, which is the one moment the pair is
/// allowed to answer.
/// </remarks>
public sealed class PlatformOperatorActExecutorTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 12, 0, 0, TimeSpan.Zero);
    private static readonly TenantId Tenant = TenantId.From(Guid.CreateVersion7());

    private static OperatorProvisionTenantCommand Provision(TenantId? tenant = null) => new(
        tenant ?? Tenant, "acme", "Acme", OrganizationId.From(Guid.CreateVersion7()), "main", "Main");

    [Fact]
    public async Task An_Admitted_Act_Runs_On_A_Resolved_Ambient_Context_Of_The_Tenant_It_Names_And_Nothing_Else()
    {
        await using var rig = new Rig();
        var admission = TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now);

        var result = await rig.Executor.ExecuteAsync(admission, Tenant, Provision());

        result.IsSuccess.Should().BeTrue();
        var seen = rig.Observed.Should().ContainSingle().Subject;
        seen.Context.IsResolved.Should().BeTrue();
        seen.Context.TenantId.Should().Be(Tenant);
        seen.Context.Origin.Should().Be(TenantContextOrigin.Ambient);
        seen.Context.UserId.Should().BeNull("an operator act names no user; the business actor is the registry actor");
        seen.Context.OrganizationId.Should().BeNull();
        seen.Context.HostScope.Should().BeNull();
        seen.Descriptor.Should().NotBeNull();
        seen.Descriptor!.SourceKind.Should().Be(AuthorizationSourceKind.PlatformOperator);
        seen.Descriptor.RequestType.Should().Be(typeof(OperatorProvisionTenantCommand));
        seen.Descriptor.Operator!.ReasonSlug.Should().Be("tenant_provision");
        seen.Descriptor.Operator.Issuer.Should().Be(TestPlatformAdmissions.Issuer);
        seen.MatchedWhileLive.Should().BeTrue("the descriptor answers for its request in its context while the act is in flight");
    }

    [Fact]
    public async Task The_Context_And_The_Provenance_Are_Gone_When_The_Act_Is()
    {
        await using var rig = new Rig();
        var before = Substitute.For<ITenantContext>();
        rig.Accessor.Current = before;

        await rig.Executor.ExecuteAsync(
            TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now), Tenant, Provision());

        rig.Accessor.Current.Should().BeSameAs(before, "the executor restores what it found, it does not clear it");
        var seen = rig.Observed.Single();
        seen.Descriptor!.IsActive.Should().BeFalse("a descriptor kept past its act proves nothing");
        seen.Descriptor.Matches(typeof(OperatorProvisionTenantCommand), seen.Context).Should().BeFalse();
        seen.Source.Capture(typeof(OperatorProvisionTenantCommand), seen.Context).Should().BeNull(
            "the frame is closed, so the same source in the same context answers nothing after the act");
    }

    [Fact]
    public async Task A_Failing_Act_Still_Restores_The_Context_And_Closes_The_Provenance()
    {
        await using var rig = new Rig { Failure = new InvalidOperationException("handler failed") };

        var act = () => rig.Executor.ExecuteAsync(
            TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now), Tenant, Provision());

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("handler failed");
        rig.Accessor.Current.Should().BeNull();
        rig.Observed.Single().Descriptor!.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task The_Source_Answers_Only_For_The_Operation_The_Admission_Is_For_And_Only_In_Its_Own_Context()
    {
        await using var rig = new Rig();
        rig.Probe = (provider, context) =>
        [
            provider.Capture(typeof(OperatorProvisionTenantCommand), context),
            provider.Capture(typeof(OperatorMapHostCommand), context),
            provider.Capture(typeof(ProvisionTenantCommand), context),
            provider.Capture(typeof(OperatorProvisionTenantCommand), Substitute.For<ITenantContext>()),
            provider.Capture(typeof(MapHostToTenantCommand), context),
        ];

        await rig.Executor.ExecuteAsync(
            TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now), Tenant, Provision());

        var answers = rig.Observed.Single().Probed;
        answers[0].Should().NotBeNull("the admitted operation's own request in the context the executor installed");
        answers[1].Should().BeNull("a host mapping is another operation: one admission, one act");
        answers[2].Should().BeNull("the seed command is not an operator act, whatever the context");
        answers[3].Should().BeNull("a context the executor did not install is not the act's");
        answers[4].Should().BeNull("a request whose policy does not name the operator source is not captured, even when the catalog lists it");
    }

    [Fact]
    public async Task A_Request_Outside_The_Catalog_Is_Refused_Before_The_Admission_Is_Spent()
    {
        await using var rig = new Rig();
        var admission = TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now);
        var seed = new ProvisionTenantCommand(Tenant, "acme", "Acme", OrganizationId.From(Guid.CreateVersion7()), "main", "Main");

        var act = () => rig.Executor.ExecuteAsync(admission, Tenant, seed);

        await act.Should().ThrowAsync<InvalidOperationException>();
        rig.Observed.Should().BeEmpty("the sender was never reached");
        admission.TryConsume().Should().BeTrue("a refusal before the act leaves the admission unspent");
    }

    [Theory]
    [InlineData("host_map")]
    [InlineData("killswitch_toggle")]
    public async Task An_Admission_For_Another_Operation_Does_Not_Admit_The_Act(string operation)
    {
        await using var rig = new Rig();
        var other = operation == "host_map" ? PlatformOperations.HostMap : PlatformOperations.KillswitchToggle;
        var admission = TestPlatformAdmissions.Admission(other, issuedAt: Now);

        var act = () => rig.Executor.ExecuteAsync(admission, Tenant, Provision());

        await act.Should().ThrowAsync<InvalidOperationException>();
        rig.Observed.Should().BeEmpty();
        admission.TryConsume().Should().BeTrue();
    }

    [Fact]
    public async Task The_Platform_Sentinel_And_An_Unassigned_Tenant_Are_Not_Tenants_An_Act_May_Name()
    {
        await using var rig = new Rig();

        // An id nobody assigned. Vogen refuses `default` and `new` at compile time; an array element the
        // runtime zeroed is the state a deserialized DTO's unset field reaches in production.
        var slot = new TenantId[1];
        var unassigned = slot[0];
        foreach (var tenant in new[] { TenantId.PlatformSentinel, unassigned })
        {
            var admission = TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now);
            var act = () => rig.Executor.ExecuteAsync(admission, tenant, Provision(tenant));
            await act.Should().ThrowAsync<InvalidOperationException>();
            admission.TryConsume().Should().BeTrue();
        }

        rig.Observed.Should().BeEmpty();
    }

    [Fact]
    public async Task A_Stale_Admission_Is_Refused_And_Is_Not_Spent_By_The_Refusal()
    {
        await using var rig = new Rig();
        var admission = TestPlatformAdmissions.Admission(
            PlatformOperations.TenantProvision, issuedAt: Now - PlatformAdmission.MaximumAge - TimeSpan.FromSeconds(1));

        var act = () => rig.Executor.ExecuteAsync(admission, Tenant, Provision());

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*stale*");
        rig.Observed.Should().BeEmpty();
        admission.TryConsume().Should().BeTrue("the age check comes before the spend, so a stale admission is simply stale");
    }

    [Fact]
    public async Task One_Admission_Admits_One_Act()
    {
        await using var rig = new Rig();
        var admission = TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now);
        await rig.Executor.ExecuteAsync(admission, Tenant, Provision());

        var again = () => rig.Executor.ExecuteAsync(admission, Tenant, Provision());

        (await again.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*already used*");
        rig.Observed.Should().ContainSingle("the second act never reached the pipeline");
    }

    [Fact]
    public async Task Each_Act_Is_Counted_By_How_It_Ended()
    {
        using var counted = new ActCounter();
        await using var rig = new Rig();

        await rig.Executor.ExecuteAsync(
            TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now), Tenant, Provision());
        rig.Replay = true;
        await rig.Executor.ExecuteAsync(
            TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now), Tenant, Provision());
        rig.Replay = false;
        rig.Refusal = new Error(new LocalizedMessage("lockey_business_rule_violation"));
        await rig.Executor.ExecuteAsync(
            TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now), Tenant, Provision());
        rig.Refusal = null;
        rig.Failure = new InvalidOperationException("handler failed");
        var failed = () => rig.Executor.ExecuteAsync(
            TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now), Tenant, Provision());
        await failed.Should().ThrowAsync<InvalidOperationException>();

        counted.Outcomes.Should().Equal(
            ["tenancy.tenant.create:applied", "tenancy.tenant.create:replayed", "tenancy.tenant.create:refused", "tenancy.tenant.create:failed"]);
    }

    [Fact]
    public async Task An_Admitted_Act_The_Pipeline_Refuses_As_Forbidden_Is_Logged_As_A_Defect()
    {
        await using var rig = new Rig();
        rig.Refusal = new Error(new LocalizedMessage("lockey_forbidden"));

        await rig.Executor.ExecuteAsync(
            TestPlatformAdmissions.Admission(PlatformOperations.TenantProvision, issuedAt: Now), Tenant, Provision());

        rig.LoggedEvents.Should().Equal([7109], "the warning an operator can alert on, since this refusal writes no audit row");
    }

    [Fact]
    public void The_Act_Catalog_Refuses_An_Operation_That_Needs_The_Platform_Role_Or_No_Acts_At_All()
    {
        var bypass = () => new PlatformOperatorActCatalog(
            [new KeyValuePair<Type, PlatformOperatorAct>(typeof(OperatorProvisionTenantCommand), new(PlatformOperations.KillswitchToggle))]);
        var empty = () => new PlatformOperatorActCatalog([]);

        bypass.Should().Throw<ArgumentException>("the platform role is the scope facade's, never a pipeline act's");
        empty.Should().Throw<ArgumentException>();
    }

    private sealed record Observation(
        ITenantContext Context,
        ITrustedOperationSource Source,
        TrustedOperationDescriptor? Descriptor,
        bool MatchedWhileLive,
        IReadOnlyList<TrustedOperationDescriptor?> Probed);

    private sealed class Rig : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;

        public Rig()
        {
            var catalog = new PlatformOperatorActCatalog(
            [
                new(typeof(OperatorProvisionTenantCommand), new(PlatformOperations.TenantProvision)),
                new(typeof(OperatorMapHostCommand), new(PlatformOperations.HostMap)),
                // A request whose policy admits the seed source and not the operator's: bound in the catalog
                // here so the source's own check of the policy's sources is what has to refuse it.
                new(typeof(MapHostToTenantCommand), new(PlatformOperations.TenantProvision)),
            ]);
            var clock = Substitute.For<IClock>();
            clock.UtcNow.Returns(Now);
            var services = new ServiceCollection();
            services.AddSingleton<ITenantContextAccessor>(Accessor);
            services.AddSingleton<IOperationPolicyCatalog>(
                new OperationPolicyCatalog([new TenancyOperationPolicySource(), new IdentityOperationPolicySource()]));
            services.AddSingleton(clock);
            services.AddSingleton(typeof(ILogger<>), typeof(CapturingLogger<>));
            services.AddSingleton(LoggedEvents);
            services.AddScoped(_ => new OperatorActGuard(
                Substitute.For<IOperationInvocationContext>(), Substitute.For<ITenantContext>(),
                Substitute.For<IUnitOfWork>(), Substitute.For<IIdentityMutationLocks>()));
            services.AddPlatformOperatorActs(catalog);
            services.AddScoped(provider =>
            {
                var sender = Substitute.For<ISender>();
                sender.Send(Arg.Any<IRequest<Result<ProvisionedTenantDto>>>(), Arg.Any<CancellationToken>()).Returns(_ =>
                {
                    var context = Accessor.Current!;
                    var source = provider.GetServices<ITrustedOperationSource>().Single();
                    var descriptor = source.Capture(typeof(OperatorProvisionTenantCommand), context);
                    Observed.Add(new Observation(
                        context, source, descriptor,
                        descriptor?.Matches(typeof(OperatorProvisionTenantCommand), context) ?? false,
                        Probe?.Invoke(source, context) ?? []));
                    if (Replay)
                    {
                        provider.GetRequiredService<OperatorActGuard>().MarkReplayed();
                    }

                    if (Failure is not null)
                    {
                        return Task.FromException<Result<ProvisionedTenantDto>>(Failure);
                    }

                    return Task.FromResult(Refusal is { } refusal
                        ? Result.FailFor<Result<ProvisionedTenantDto>>(refusal)
                        : Result.Ok(new ProvisionedTenantDto(Tenant, "acme", OrganizationId.From(Guid.CreateVersion7()), "main")));
                });
                return sender;
            });
            _provider = services.BuildServiceProvider();
            Executor = _provider.GetRequiredService<PlatformOperatorActExecutor>();
        }

        public StaticTenantContextAccessor Accessor { get; } = new(null);

        public PlatformOperatorActExecutor Executor { get; }

        public List<Observation> Observed { get; } = [];

        public Exception? Failure { get; set; }

        public bool Replay { get; set; }

        public Error? Refusal { get; set; }

        public List<int> LoggedEvents { get; } = [];

        public Func<ITrustedOperationSource, ITenantContext, IReadOnlyList<TrustedOperationDescriptor?>>? Probe { get; set; }

        public ValueTask DisposeAsync() => _provider.DisposeAsync();
    }

    private sealed class CapturingLogger<T>(List<int> events) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            events.Add(eventId.Id);
    }

    /// <summary>Records the <c>operation:outcome</c> of every act counted while it is alive.</summary>
    private sealed class ActCounter : IDisposable
    {
        private readonly MeterListener _listener = new();

        public ActCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == PlatformOperatorMetrics.MeterName && instrument.Name == PlatformOperatorMetrics.ActCounterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                string? operation = null, outcome = null;
                foreach (var tag in tags)
                {
                    if (tag.Key == "operation") operation = tag.Value as string;
                    if (tag.Key == "outcome") outcome = tag.Value as string;
                }

                lock (Outcomes)
                {
                    Outcomes.Add(operation + ":" + outcome);
                }
            });
            _listener.Start();
        }

        public List<string> Outcomes { get; } = [];

        public void Dispose() => _listener.Dispose();
    }
}
