using System.Diagnostics.CodeAnalysis;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Application.Pipeline;
using LearnStack.Infrastructure.Audit;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace LearnStack.Tests.Unit.Application.Jobs;

public sealed class BackgroundJobExecutionAdmissionTests
{
    [Fact]
    public async Task Policy_source_captures_the_admitted_frame_without_reclaiming_the_root_and_retains_only_live_ancestors()
    {
        var fixture = new Fixture();
        var receipt = JobAdmissionFixtureReceipt.Create(Fixture.Key, fixture.Context.TenantId, null,
            BackgroundJobExecutionStage.Transactional);
        using var binding = receipt.SourceOwner.Enter();
        using var execution = fixture.EnterReceipt(receipt);
        using var source = new BackgroundJobOperationSource();
        source.Capture(typeof(Root), fixture.Context).Should().BeNull("execution alone has no admitted request frame");
        BackgroundJobExecutionAdmission.TryEnterRequest(typeof(Root), fixture.Context, out var root).Should().BeTrue();
        using (root)
        {
            var parent = source.Capture(typeof(Root), fixture.Context);
            parent.Should().NotBeNull();
            parent!.SourceKind.Should().Be(AuthorizationSourceKind.BackgroundJob);
            parent.Matches(typeof(Root), fixture.Context).Should().BeTrue();
            parent.Matches(typeof(Nested), fixture.Context).Should().BeFalse();
            var auxiliary = receipt.SourceOwner.BeginAuthorizationRead();
            auxiliary.HoldActualTask(Task.CompletedTask);
            parent.IsActive.Should().BeTrue("owner lifetime is distinct from temporary auxiliary-lane readiness");
            await auxiliary.CleanupAsync();
            source.Capture(typeof(Unlisted), fixture.Context).Should().BeNull();
            source.Capture(typeof(Root), ((JobContext)fixture.Context) with { }).Should().BeNull();
            BackgroundJobExecutionAdmission.PermitsRequest(typeof(Root), fixture.Context).Should().BeFalse(
                "capturing slot-5 evidence must not attempt slot-4 root admission again");
            BackgroundJobExecutionAdmission.TryEnterRequest(typeof(Nested), fixture.Context, out var nested).Should().BeTrue();
            TrustedOperationDescriptor? child;
            using (nested)
            {
                parent.IsActive.Should().BeTrue("an open ancestor retains its own lifetime during a legitimate nested act");
                child = source.Capture(typeof(Nested), fixture.Context);
                child.Should().NotBeNull();
                child!.IsActive.Should().BeTrue();
            }
            child!.IsActive.Should().BeFalse();
            parent.IsActive.Should().BeTrue();
            root!.Dispose();
            parent.IsActive.Should().BeFalse();
            source.Capture(typeof(Root), fixture.Context).Should().BeNull();
        }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("binding")]
    [InlineData("execution")]
    [InlineData("context")]
    [InlineData("owner-fault")]
    public async Task Policy_source_revokes_retained_descriptors_when_their_actual_owner_closes(string closing)
    {
        var fixture = new Fixture();
        var receipt = JobAdmissionFixtureReceipt.Create(Fixture.Key, fixture.Context.TenantId, null,
            BackgroundJobExecutionStage.Transactional);
        using var binding = receipt.SourceOwner.Enter();
        using var execution = fixture.EnterReceipt(receipt);
        using var source = new BackgroundJobOperationSource();
        BackgroundJobExecutionAdmission.TryEnterRequest(typeof(Root), fixture.Context, out var request).Should().BeTrue();
        using var requestScope = request;
        var descriptor = source.Capture(typeof(Root), fixture.Context);
        descriptor.Should().NotBeNull();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inherited = Task.Run(async () =>
        {
            await release.Task;
            descriptor!.IsActive.Should().BeFalse();
            source.Capture(typeof(Root), fixture.Context).Should().BeNull();
        });
        if (closing == "source") source.Dispose();
        else if (closing == "binding") binding.Dispose();
        else if (closing == "execution") execution.Dispose();
        else if (closing == "context") fixture.Context = ((JobContext)fixture.Context) with { };
        else
        {
            var lane = receipt.SourceOwner.BeginAuthorizationEvidence();
            lane.HoldActualTask(Task.CompletedTask);
            lane.MarkCleanupFailed();
            await lane.CleanupAsync();
        }
        release.SetResult();
        await inherited.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task External_scope_refuses_sender_and_direct_owner_entry_after_accessor_replacement_and_inherited_cleanup()
    {
        var fixture = new Fixture();
        using var provider = fixture.Services();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task inherited;
        using (BackgroundJobExecutionAdmission.EnterExternal())
        {
            fixture.Context = new JobContext(TenantId.From(Guid.CreateVersion7()));
            (await sender.Send(new Root(new Nested()))).Error!.Code.Should().Be("forbidden");
            (await sender.Send(new FrameworkWrite())).Error!.Code.Should().Be("forbidden");
            var directOwner = () => BackgroundJobExecutionAdmission.RequireContext(fixture.Context);
            directOwner.Should().Throw<InvalidOperationException>();
            var bypass = () => BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
            bypass.Should().Throw<InvalidOperationException>();
            inherited = Task.Run(async () =>
            {
                await release.Task;
                (await sender.Send(new Root(new Nested()))).Error!.Code.Should().Be("forbidden");
                directOwner.Should().Throw<InvalidOperationException>();
                bypass.Should().Throw<InvalidOperationException>();
            });
        }
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
        BackgroundJobExecutionAdmission.RequireContext(fixture.Context);
        release.SetResult();
        await inherited.WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Constructed.Should().BeEmpty("neither live nor retained provider work may construct a MediatR handler");
    }

    [Theory]
    [InlineData(BackgroundJobExecutionStage.Transactional, BackgroundJobCheckpointKind.Prepare)]
    [InlineData(BackgroundJobExecutionStage.Prepare, BackgroundJobCheckpointKind.Finalize)]
    [InlineData(BackgroundJobExecutionStage.Finalize, BackgroundJobCheckpointKind.Transactional)]
    public void Ordinary_participant_rejects_another_stage_boundary(BackgroundJobExecutionStage stage, BackgroundJobCheckpointKind kind)
    {
        var fixture = new Fixture();
        var unit = Substitute.For<IUnitOfWork>();
        var participant = new ProbeCompletion(unit, new StaticTenantContextAccessor(fixture.Context));
        var receipt = JobAdmissionFixtureReceipt.Create(Fixture.Key, fixture.Context.TenantId, null, stage);
        var arm = () => participant.Arm(receipt, receipt.Run.OpenBoundary(kind));
        arm.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Real_sender_constructs_only_admitted_root_nested_and_framework_handlers()
    {
        var fixture = new Fixture();
        using var provider = fixture.Services();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using var admission = fixture.Enter();

        (await sender.Send(new Root(new Nested()))).IsSuccess.Should().BeTrue();
        (await sender.Send(new Root(new Nested()))).Error!.Code.Should().Be("forbidden", "one receipt claims one ordinary root even after success");
        (await sender.Send(new FrameworkRead())).IsSuccess.Should().BeTrue();
        (await sender.Send(new FrameworkWrite())).IsSuccess.Should().BeTrue();
        fixture.Constructed.Should().BeEquivalentTo([typeof(Root), typeof(Nested), typeof(FrameworkRead), typeof(FrameworkWrite)]);
    }

    [Theory]
    [InlineData("receipt")]
    [InlineData("unit")]
    [InlineData("accessor_reference")]
    [InlineData("closed_gate")]
    public async Task A_participant_refuses_substituted_receipt_unit_context_or_closed_gate_before_begin(string changed)
    {
        var fixture = new Fixture();
        var accessor = new StaticTenantContextAccessor(fixture.Context);
        var unit = Substitute.For<IUnitOfWork>();
        var participant = new ProbeCompletion(unit, accessor);
        var receipt = JobAdmissionFixtureReceipt.Create(Fixture.Key, fixture.Context.TenantId, null,
            BackgroundJobExecutionStage.Transactional);
        var gate = receipt.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional);
        participant.Arm(receipt, gate);
        if (changed == "accessor_reference") accessor.Current = ((JobContext)fixture.Context) with { };
        if (changed == "closed_gate") gate.Close();
        var substitute = changed == "receipt" ? JobAdmissionFixtureReceipt.Create(Fixture.Key, fixture.Context.TenantId,
            null, BackgroundJobExecutionStage.Transactional) : receipt;
        var other = changed == "unit" ? Substitute.For<IUnitOfWork>() : unit;
        var activate = () => fixture.Admission.Enter(substitute, other, participant);
        activate.Should().Throw<InvalidOperationException>();
        await unit.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default(TransactionMode), default);
        await other.DidNotReceiveWithAnyArgs().BeginTransactionAsync(default(TransactionMode), default);
    }

    [Theory]
    [InlineData(typeof(Unlisted))]
    [InlineData(typeof(Platform))]
    [InlineData(typeof(Privileged))]
    [InlineData(typeof(Provisioning))]
    [InlineData(typeof(UnresolvedMarked))]
    [InlineData(typeof(PlatformAudited))]
    [InlineData(typeof(MissingAudit))]
    public async Task Real_sender_refuses_disallowed_roots_and_nested_sends_before_construction(Type refusedType)
    {
        var fixture = new Fixture();
        using var provider = fixture.Services();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        var request = (IRequest<Result<string>>)Activator.CreateInstance(refusedType)!; // All probes are parameterless requests.
        using var admission = fixture.Enter();

        (await sender.Send(request)).Error!.Code.Should().Be("forbidden");
        (await sender.Send(new Root(request))).Error!.Code.Should().Be("forbidden");
        fixture.Constructed.Should().Equal(typeof(Root));
    }

    [Theory]
    [InlineData("unresolved")]
    [InlineData("claims")]
    [InlineData("tenant")]
    [InlineData("organization")]
    public async Task Accessor_replacement_and_a_fresh_DI_scope_cannot_remove_or_retarget_admission(string replacement)
    {
        var fixture = new Fixture();
        using var provider = fixture.Services();
        using var admission = fixture.Enter();
        fixture.Context = replacement switch
        {
            "unresolved" => UnresolvedTenantContext.Instance,
            "claims" => ((JobContext)fixture.Context) with { Origin = TenantContextOrigin.HostAndClaim },
            "tenant" => ((JobContext)fixture.Context) with { TenantId = TenantId.From(Guid.CreateVersion7()) },
            _ => ((JobContext)fixture.Context) with { OrganizationId = OrganizationId.From(Guid.CreateVersion7()) },
        };
        using var fresh = provider.CreateScope();

        (await fresh.ServiceProvider.GetRequiredService<ISender>().Send(new Nested())).Error!.Code.Should().Be("forbidden");
        (await fresh.ServiceProvider.GetRequiredService<ISender>().Send(new Provisioning())).Error!.Code.Should().Be("forbidden");
        fixture.Constructed.Should().BeEmpty();
        var retarget = () => BackgroundJobExecutionAdmission.RequireContext(fixture.Context);
        retarget.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Closing_admission_revokes_retained_child_flows_without_leaking_into_the_caller()
    {
        var fixture = new Fixture();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> retained;
        using (fixture.Enter())
        {
            var reopen = () => fixture.Enter();
            reopen.Should().Throw<InvalidOperationException>();
            retained = Task.Run(async () =>
            {
                await release.Task;
                var privileged = () => BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
                privileged.Should().Throw<InvalidOperationException>();
                return BackgroundJobExecutionAdmission.PermitsRequest(typeof(Nested), fixture.Context);
            });
        }
        release.SetResult();
        (await retained).Should().BeFalse();
        BackgroundJobExecutionAdmission.PermitsRequest(typeof(Unlisted), fixture.Context).Should().BeTrue();
        BackgroundJobExecutionAdmission.RefusePrivilegedEntry();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_retained_nested_flow_cannot_outlive_its_ordinary_parent(bool closeParent)
    {
        var fixture = new Fixture();
        using var provider = fixture.Services();
        using var serviceScope = provider.CreateScope();
        var sender = serviceScope.ServiceProvider.GetRequiredService<ISender>();
        using var admission = fixture.Enter();
        BackgroundJobExecutionAdmission.TryEnterRequest(typeof(Root), fixture.Context, out var rootScope)
            .Should().BeTrue();
        using var root = rootScope!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var retained = Task.Run(async () =>
        {
            BackgroundJobExecutionAdmission.TryEnterRequest(typeof(Nested), fixture.Context, out var nestedScope)
                .Should().BeTrue();
            using var nested = nestedScope!;
            entered.SetResult();
            await resume.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var nestedResult = await sender.Send(new Nested());
            var frameworkResult = await sender.Send(new FrameworkWrite());
            var ownerFailure = Record.Exception(() => BackgroundJobExecutionAdmission.RequireContext(fixture.Context));
            return (nestedResult, frameworkResult, ownerFailure);
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (closeParent) root.Dispose();
        }
        finally
        {
            resume.TrySetResult();
        }
        var observed = await retained.WaitAsync(TimeSpan.FromSeconds(10));

        if (closeParent)
        {
            observed.nestedResult.Error?.Code.Should().Be("forbidden");
            observed.frameworkResult.Error?.Code.Should().Be("forbidden");
            observed.ownerFailure.Should().BeOfType<InvalidOperationException>();
            fixture.Constructed.Should().BeEmpty("closed ancestors revoke before handler construction");
        }
        else
        {
            observed.nestedResult.IsSuccess.Should().BeTrue();
            observed.frameworkResult.IsSuccess.Should().BeTrue();
            observed.ownerFailure.Should().BeNull();
            fixture.Constructed.Should().Equal(typeof(Nested), typeof(FrameworkWrite));
        }
        root.Dispose();
        BackgroundJobExecutionAdmission.RequireContext(fixture.Context);
    }

    [Fact]
    public async Task External_stage_roots_cannot_be_sent_as_nested_or_from_another_stage()
    {
        var fixture = new Fixture();
        fixture.Catalog.Profile = BackgroundJobProfileRegistration.External(typeof(Root), typeof(FinalizeProbe),
            typeof(TenantId), typeof(TenantId), typeof(IDisposable));
        using var provider = fixture.Services();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using (fixture.Enter(BackgroundJobExecutionStage.Prepare))
        {
            (await sender.Send(new FinalizeProbe())).Error!.Code.Should().Be("forbidden");
            (await sender.Send(new Root(new FinalizeProbe()))).Error!.Code.Should().Be("forbidden");
            (await sender.Send(new Root(new Nested()))).Error!.Code.Should().Be("forbidden");
        }
        using (fixture.Enter(BackgroundJobExecutionStage.Finalize))
        {
            (await sender.Send(new Root(new Nested()))).Error!.Code.Should().Be("forbidden");
            (await sender.Send(new Nested())).Error!.Code.Should().Be("forbidden");
            (await sender.Send(new FinalizeProbe())).IsSuccess.Should().BeTrue();
        }
        fixture.Constructed.Count(type => type == typeof(FinalizeProbe)).Should().Be(1);
    }

    [Theory]
    [InlineData(BackgroundJobScope.TenantWide)]
    [InlineData(BackgroundJobScope.Organization)]
    public async Task One_exact_framework_request_inherits_each_verified_route(BackgroundJobScope route)
    {
        var fixture = new Fixture();
        fixture.Catalog.Scope = route;
        if (route == BackgroundJobScope.Organization)
            fixture.Context = ((JobContext)fixture.Context) with { OrganizationId = OrganizationId.From(Guid.CreateVersion7()) };
        using var provider = fixture.Services();
        using var scope = provider.CreateScope();
        using var admission = fixture.Enter();
        (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new FrameworkRead())).IsSuccess.Should().BeTrue();
        (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new FrameworkWrite())).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task Admission_freezes_request_metadata_before_a_catalogue_can_be_widened()
    {
        var fixture = new Fixture();
        using var provider = fixture.Services();
        using var scope = provider.CreateScope();
        using var admission = fixture.Enter();
        fixture.Catalog.Overrides[typeof(Unlisted)] = new(typeof(Unlisted), BackgroundJobRequestClassification.FrameworkReadWrite, null);
        fixture.Catalog.Overrides[typeof(FrameworkRead)] = new(typeof(FrameworkRead), BackgroundJobRequestClassification.FrameworkReadWrite, null);
        BackgroundJobExecutionAdmission.ModeFor(typeof(ReadBackgroundJobEnvelopeQuery), fixture.Catalog).Should().Be(TransactionMode.ReadOnly);
        (await scope.ServiceProvider.GetRequiredService<ISender>().Send(new Unlisted())).Error!.Code.Should().Be("forbidden");
        fixture.Constructed.Should().BeEmpty();
    }

    [Fact]
    public async Task Cancellation_unwinds_request_position_without_removing_the_ordinary_ceiling()
    {
        var fixture = new Fixture();
        using var provider = fixture.Services();
        using var scope = provider.CreateScope();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        using var admission = fixture.Enter();
        var canceled = async () => await sender.Send(new Root(new Nested()), new CancellationToken(canceled: true));
        await canceled.Should().ThrowAsync<OperationCanceledException>();
        (await sender.Send(new Root(new Nested()))).Error!.Code.Should().Be("forbidden");
        (await sender.Send(new Unlisted())).Error!.Code.Should().Be("forbidden");
    }

    [Fact]
    public void Framework_mode_is_exact_and_does_not_depend_on_a_job_or_public_host_marker()
    {
        var fixture = new Fixture();
        BackgroundJobExecutionAdmission.ModeFor(typeof(ReadBackgroundJobEnvelopeQuery), fixture.Catalog).Should().Be(TransactionMode.ReadOnly);
        BackgroundJobExecutionAdmission.ModeFor(typeof(FrameworkWrite), fixture.Catalog).Should().Be(TransactionMode.ReadWrite);
        BackgroundJobExecutionAdmission.ModeFor(typeof(Nested), fixture.Catalog).Should().Be(TransactionMode.ReadWrite);
        BackgroundJobExecutionAdmission.ModeFor(typeof(Unlisted), fixture.Catalog).Should().Be(TransactionMode.ReadWrite);
        typeof(FrameworkRead).IsDefined(typeof(PublicSurfaceAttribute), false).Should().BeFalse();
    }

    [Fact]
    public void Activation_refuses_pending_unknown_sentinel_and_mismatched_organization_scope()
    {
        var fixture = new Fixture();
        foreach (var action in new Action[]
                 {
                     () => fixture.EnterReceipt(JobAdmissionFixtureReceipt.Create("tenancy.unknown.v1", fixture.Context.TenantId, null, BackgroundJobExecutionStage.Transactional)),
                     () => fixture.EnterReceipt(JobAdmissionFixtureReceipt.Create(Fixture.Key, TenantId.PlatformSentinel, null, BackgroundJobExecutionStage.Transactional)),
                     () => fixture.EnterReceipt(JobAdmissionFixtureReceipt.Create(Fixture.Key, fixture.Context.TenantId, OrganizationId.From(Guid.CreateVersion7()), BackgroundJobExecutionStage.Transactional)),
                 }) action.Should().Throw<InvalidOperationException>();
        fixture.Catalog.Readiness = BackgroundJobDefinitionReadiness.Pending;
        var pending = () => fixture.Enter();
        pending.Should().Throw<InvalidOperationException>();
    }

    public sealed record Root(IRequest<Result<string>> Next) : IRequest<Result<string>>;
    public sealed record Nested : IRequest<Result<string>>;
    public sealed record FrameworkRead : IRequest<Result<string>>;
    public sealed record FrameworkWrite : IRequest<Result<string>>;
    public sealed record Unlisted : IRequest<Result<string>>;
    public sealed record Platform : IRequest<Result<string>>;
    public sealed record Privileged : IRequest<Result<string>>;
    public sealed record PlatformAudited : IRequest<Result<string>>;
    public sealed record MissingAudit : IRequest<Result<string>>;
    public sealed record FinalizeProbe : IRequest<Result<string>>;
    public sealed record Provisioning : IRequest<Result<string>>, IProvisionsTenant
    {
        public TenantId ProvisioningTenantId => TenantId.From(Guid.CreateVersion7());
    }
    [AllowsUnresolvedTenantContext]
    public sealed record UnresolvedMarked : IRequest<Result<string>>;

    public sealed class RootHandler(Fixture fixture, ISender sender) : IRequestHandler<Root, Result<string>>
    {
        private readonly Fixture _fixture = Construct(fixture, typeof(Root));
        public Task<Result<string>> Handle(Root request, CancellationToken cancellationToken)
        {
            _ = _fixture;
            return sender.Send(request.Next, cancellationToken);
        }
    }

    public sealed class Handler<T> : IRequestHandler<T, Result<string>> where T : IRequest<Result<string>>
    {
        public Handler(Fixture fixture) => fixture.Constructed.Add(typeof(T));
        public Task<Result<string>> Handle(T request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Result.Ok("ran"));
        }
    }

    private static Fixture Construct(Fixture fixture, Type request)
    {
        fixture.Constructed.Add(request);
        return fixture;
    }

    public sealed class Fixture
    {
        public const string Key = "tenancy.admission-probe.v1";
        private readonly StaticTenantContextAccessor _accessor = new StaticTenantContextAccessor(new JobContext(TenantId.From(Guid.CreateVersion7())));
        public ITenantContext Context
        {
            get => _accessor.Current ?? UnresolvedTenantContext.Instance;
            set => _accessor.Current = value;
        }
        public List<Type> Constructed { get; } = [];
        public Catalog Catalog { get; } = new();
        public BackgroundJobExecutionAdmission Admission { get; }
        public Fixture() => Admission = new(Catalog, Catalog, new AuditCatalog([new Source()]));
        public IDisposable Enter(BackgroundJobExecutionStage stage = BackgroundJobExecutionStage.Transactional) =>
            EnterReceipt(JobAdmissionFixtureReceipt.Create(Key, Context.TenantId, Context.OrganizationId, stage));
        public IDisposable EnterReceipt(ConfirmedStartReceipt receipt)
        {
            var unit = Substitute.For<IUnitOfWork>();
            var participant = new ProbeCompletion(unit, _accessor);
            participant.Arm(receipt, receipt.Run.OpenBoundary(Enum.Parse<BackgroundJobCheckpointKind>(
                (receipt.Stage ?? throw new InvalidOperationException("This fixture requires an ordinary root.")).ToString())));
            return Admission.Enter(receipt, unit, participant);
        }
        public ServiceProvider Services()
        {
            var services = new ServiceCollection().AddSingleton(this).AddTransient(_ => Context);
            services.AddMediatR(configuration =>
            {
                configuration.RegisterServicesFromAssembly(typeof(ITenantContext).Assembly);
                configuration.AddOpenBehavior(typeof(TenantContextBehavior<,>));
                configuration.AddOpenBehavior(typeof(AuthorizationBehavior<,>));
            });
            services.AddTransient<IRequestHandler<Root, Result<string>>, RootHandler>();
            foreach (var type in Catalog.Types.Append(typeof(Unlisted)))
                if (type != typeof(Root)) services.AddTransient(typeof(IRequestHandler<,>).MakeGenericType(type, typeof(Result<string>)),
                    typeof(Handler<>).MakeGenericType(type));
            return services.BuildServiceProvider();
        }
    }

    private sealed class ProbeCompletion(IUnitOfWork unit, ITenantContextAccessor accessor)
        : BackgroundJobCompletionParticipant(unit, accessor)
    {
        protected override Task PreflightAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task FenceAsync(IResultBase response, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    public sealed class Catalog : IBackgroundJobDefinitionCatalog, IBackgroundJobRequestCatalog
    {
        public Type[] Types { get; } = [typeof(Root), typeof(Nested), typeof(FrameworkRead), typeof(FrameworkWrite),
            typeof(Platform), typeof(Privileged), typeof(Provisioning), typeof(UnresolvedMarked), typeof(PlatformAudited), typeof(MissingAudit), typeof(FinalizeProbe)];
        public BackgroundJobDefinitionReadiness Readiness { get; set; } = BackgroundJobDefinitionReadiness.Active;
        public BackgroundJobScope Scope { get; set; } = BackgroundJobScope.TenantWide;
        public BackgroundJobProfileRegistration Profile { get; set; } = BackgroundJobProfileRegistration.Transactional(typeof(Root));
        public Dictionary<Type, BackgroundJobRequestDescriptor> Overrides { get; } = [];
        public IReadOnlyCollection<BackgroundJobRequestDescriptor> All => Types.Concat(Overrides.Keys).Distinct().Select(type =>
        {
            TryGet(type, out var descriptor);
            return descriptor!; // Types enumerates only registered probe metadata.
        }).ToArray();
        public bool TryGet(string key, [NotNullWhen(true)] out BackgroundJobDefinitionDescriptor? definition)
        {
            definition = key == Fixture.Key ? new BackgroundJobDefinitionDescriptor(new BackgroundJobDefinitionRegistration(
                key, typeof(LearnStack.Modules.Tenancy.Application.AssemblyMarker), typeof(TenantId), Scope,
                Readiness, "probe", Profile, Types)) : null;
            return definition is not null;
        }
        public BackgroundJobDefinitionResolution Resolve(string? key) =>
            TryGet(key ?? string.Empty, out var definition) ? new(BackgroundJobDefinitionDisposition.Active, definition)
                : new(BackgroundJobDefinitionDisposition.Unknown, null);
        public bool TryGet(Type requestType, [NotNullWhen(true)] out BackgroundJobRequestDescriptor? descriptor)
        {
            if (Overrides.TryGetValue(requestType, out descriptor)) return true;
            var classification = requestType == typeof(Platform) ? BackgroundJobRequestClassification.Platform
                : requestType == typeof(Privileged) ? BackgroundJobRequestClassification.Privileged
                : requestType == typeof(FrameworkRead) ? BackgroundJobRequestClassification.FrameworkReadOnly
                : requestType == typeof(FrameworkWrite) ? BackgroundJobRequestClassification.FrameworkReadWrite
                : BackgroundJobRequestClassification.Ordinary;
            descriptor = Types.Contains(requestType) ? new(requestType, classification, classification is BackgroundJobRequestClassification.FrameworkReadOnly or BackgroundJobRequestClassification.FrameworkReadWrite
                ? null : Scope) : null;
            return descriptor is not null;
        }
    }

    private sealed record JobContext(TenantId TenantId) : ITenantContext
    {
        public bool IsResolved => true;
        public OrganizationId? OrganizationId { get; init; }
        public UserId? UserId => LearnStack.SharedKernel.Identifiers.UserId.SystemActor;
        public TenantContextOrigin? Origin { get; init; } = TenantContextOrigin.Ambient;
        public string? CorrelationId => null;
        public string? ModuleName => "tenancy";
    }

    private sealed class Source : IAuditCatalogSource
    {
        public string ModuleName => "tenancy";
        public void Describe(IAuditCatalogBuilder builder)
        {
            builder.Off<Root>().Off<Nested>().Off<FrameworkRead>().Off<FrameworkWrite>().Off<Unlisted>()
                .Off<Platform>().Off<Privileged>().Off<Provisioning>().Off<UnresolvedMarked>().Off<FinalizeProbe>();
            builder.MustAudit<PlatformAudited>("tenancy.probe.platform", OperationType.PlatformAdmin,
                typeof(LearnStack.Modules.Tenancy.Domain.Tenant));
        }
    }
}
