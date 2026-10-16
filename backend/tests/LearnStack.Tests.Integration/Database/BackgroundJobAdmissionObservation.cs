using System.Diagnostics;
using System.Data.Common;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LearnStack.Tests.Integration.Database;

/// <summary>Decorators invoke the actual framework handlers inside the real MediatR transaction.</summary>
internal sealed class BackgroundJobAdmissionObservation
{
    internal IUnitOfWork? ReadUnit { get; set; }
    internal IUnitOfWork? StartUnit { get; set; }
    internal DbConnection? ReadConnection { get; set; }
    internal DbConnection? StartConnection { get; set; }
    internal bool ReadScopeDisposed { get; set; }
    internal bool StartScopeDisposed { get; set; }
    internal bool FailCleanup { get; init; }
    internal bool FailReadCleanup { get; init; }
    internal Func<ReadBackgroundJobEnvelopeQuery, IServiceProvider, Task>? OnRead { get; init; }
    internal Func<AdmitBackgroundJobAttemptCommand, IServiceProvider, Task>? OnStart { get; init; }

    internal void Configure(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddScoped<ReadBackgroundJobEnvelopeQueryHandler>();
        services.AddScoped<AdmitBackgroundJobAttemptCommandHandler>();
        services.RemoveAll<IRequestHandler<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>>();
        services.AddScoped<IRequestHandler<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>, ObservedRead>();
        services.RemoveAll<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>>();
        services.AddScoped<IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>, ObservedStart>();
        services.AddScoped<ReadCleanup>();
        services.AddScoped<StartCleanup>();
    }

    private sealed class ObservedRead(ReadBackgroundJobEnvelopeQueryHandler actual, IServiceProvider services,
        IUnitOfWork unit, ITenantContext tenantContext, BackgroundJobAdmissionObservation observation)
        : IRequestHandler<ReadBackgroundJobEnvelopeQuery, Result<BackgroundJobEnvelope>>
    {
        public async Task<Result<BackgroundJobEnvelope>> Handle(ReadBackgroundJobEnvelopeQuery request, CancellationToken cancellationToken)
        {
            observation.ReadUnit = unit;
            observation.ReadConnection = unit.Connection;
            _ = services.GetRequiredService<ReadCleanup>();
            unit.Mode.Should().Be(TransactionMode.ReadOnly);
            tenantContext.IsResolved.Should().BeFalse("envelope validation has no installed business context");
            (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SHOW transaction_read_only")).Should().Be("on");
            (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit, "SELECT current_user")).Should().Be("learnstack_app");
            (await BackgroundJobRuntimeRows.ScalarInUnitAsync(unit,
                "SELECT (rolbypassrls OR rolsuper)::text FROM pg_roles WHERE rolname=current_user")).Should().Be("false");
            if (observation.OnRead is { } hook) await hook(request, services);
            return await actual.Handle(request, cancellationToken);
        }
    }

    private sealed class ObservedStart(AdmitBackgroundJobAttemptCommandHandler actual, IServiceProvider services,
        IUnitOfWork unit, ITenantContext tenantContext, BackgroundJobAdmissionObservation observation)
        : IRequestHandler<AdmitBackgroundJobAttemptCommand, Result<None>>
    {
        public async Task<Result<None>> Handle(AdmitBackgroundJobAttemptCommand request, CancellationToken cancellationToken)
        {
            observation.ReadScopeDisposed.Should().BeTrue("the pre-read scope is disposed before start admission");
            unit.Should().NotBeSameAs(observation.ReadUnit);
            unit.Mode.Should().Be(TransactionMode.ReadWrite);
            tenantContext.IsResolved.Should().BeTrue();
            tenantContext.UserId.Should().Be(LearnStack.SharedKernel.Identifiers.UserId.SystemActor);
            tenantContext.HostScope.Should().BeNull();
            observation.StartUnit = unit;
            observation.StartConnection = unit.Connection;
            _ = services.GetRequiredService<StartCleanup>();
            if (observation.OnStart is { } hook) await hook(request, services);
            return await actual.Handle(request, cancellationToken);
        }
    }

    private sealed class ReadCleanup(IUnitOfWork unit, BackgroundJobAdmissionObservation observation) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            unit.Transaction.Should().BeNull("the read transaction resolved before scope cleanup");
            observation.ReadScopeDisposed = true;
            if (observation.FailReadCleanup) throw new InvalidOperationException("Deliberate pre-read scope cleanup failure");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StartCleanup(IUnitOfWork unit, ITenantContextAccessor accessor,
        BackgroundJobAdmissionObservation observation) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            unit.Transaction.Should().BeNull("the admission transaction resolved before scope cleanup");
            accessor.Current!.UserId.Should().Be(LearnStack.SharedKernel.Identifiers.UserId.SystemActor);
            Activity.Current.Should().NotBeNull("actual cleanup retains the admission Activity");
            Action refuse = BackgroundJobExecutionAdmission.RefusePrivilegedEntry;
            refuse.Should().Throw<InvalidOperationException>("the framework authority survives actual scope cleanup");
            observation.StartScopeDisposed = true;
            if (observation.FailCleanup) throw new InvalidOperationException("Deliberate post-ACK scope cleanup failure");
            return ValueTask.CompletedTask;
        }
    }
}
