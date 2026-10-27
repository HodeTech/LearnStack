namespace LearnStack.Tests.Integration.Database;

internal sealed partial class BackgroundJobPositiveSdkFixture
{
    private const string ContractsSource = """
        using System;
        using LearnStack.SharedKernel.Identifiers;
        using LearnStack.SharedKernel.Jobs;
        namespace LearnStack.Modules.JobsPositiveSdkFixture.Application.Contracts;
        public sealed record Parameters(Guid ResourceId, OrganizationId PreparedOrganizationId,
            OrganizationId FinalizedOrganizationId) : IBackgroundJobParameters;
        public sealed record Plan(Guid ResourceId, OrganizationId FinalizedOrganizationId) : IBackgroundJobPlan;
        public sealed record Outcome(Guid ResourceId) : IBackgroundJobOutcome;
        public interface IOperation : IBackgroundJobExternalOperation<Plan,Outcome> { }
        """;

    private const string ApplicationSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using LearnStack.SharedKernel.Jobs;
        using LearnStack.SharedKernel.Results;
        using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
        using LearnStack.Modules.JobsPositiveSdkFixture.Application.Contracts;
        using MediatR;
        namespace LearnStack.Modules.JobsPositiveSdkFixture.Application;
        public sealed class Declarations { }
        public sealed record Submit(Parameters Parameters) : IRequest<Result<BackgroundJobId>>;
        public sealed record Prepare(Parameters Parameters) : IRequest<Result<Plan>>;
        public sealed record Finalize(Parameters Parameters, BackgroundJobFinalization<Plan,Outcome> Finalization)
            : IRequest<Result<None>>;
        public sealed class SubmitHandler(IBackgroundJobScheduler scheduler) : IRequestHandler<Submit,Result<BackgroundJobId>>
        {
            public async Task<Result<BackgroundJobId>> Handle(Submit request,CancellationToken token) =>
                Result<BackgroundJobId>.Ok(await scheduler.EnqueueAsync(
                    new BackgroundJobKey<Parameters>("jobspositivesdkfixture.run.v1"),request.Parameters,token));
        }
        public sealed class PrepareHandler(ISender sender) : IRequestHandler<Prepare,Result<Plan>>
        {
            public async Task<Result<Plan>> Handle(Prepare request,CancellationToken token)
            {
                var p=request.Parameters;
                var written=await sender.Send(new CreateFixtureOrganization(p.PreparedOrganizationId,
                    "prepared-"+p.PreparedOrganizationId.Value.ToString("N"),"Prepared provider fixture"),token);
                return written.IsFailure ? Result<Plan>.Fail(written.Error)
                    : Result<Plan>.Ok(new Plan(p.ResourceId,p.FinalizedOrganizationId));
            }
        }
        public sealed class FinalizeHandler(ISender sender) : IRequestHandler<Finalize,Result<None>>
        {
            public async Task<Result<None>> Handle(Finalize request,CancellationToken token)
            {
                var p=request.Parameters;
                var f=request.Finalization;
                if(f.Plan.ResourceId!=p.ResourceId || f.Plan.FinalizedOrganizationId!=p.FinalizedOrganizationId
                    || f.Outcome is {} outcome && outcome.ResourceId!=p.ResourceId)
                    throw new InvalidOperationException("Finalization identifiers were substituted.");
                var written=await sender.Send(new CreateFixtureOrganization(p.FinalizedOrganizationId,
                    "finalized-"+p.FinalizedOrganizationId.Value.ToString("N"),"Finalized provider "+f.Status),token);
                return written.IsFailure ? Result<None>.Fail(written.Error) : Result<None>.Ok(None.Value);
            }
        }
        """;

    private const string InfrastructureSource = """
        using System;
        using System.Threading;
        using System.Threading.Tasks;
        using LearnStack.SharedKernel.Jobs;
        using LearnStack.Modules.JobsPositiveSdkFixture.Application.Contracts;
        using LearnStack.Tests.Integration.Database;
        namespace LearnStack.Modules.JobsPositiveSdkFixture.Infrastructure;
        public sealed class Operation : IOperation
        {
            private readonly BackgroundJobPositiveProviderClient _client;
            private readonly BackgroundJobPositiveCausalReportMutation? _mutation;
            public Operation(BackgroundJobPositiveProviderClient client,BackgroundJobPositiveProviderConstructionProbe construction,
                BackgroundJobPositiveCausalReportMutation? mutation=null)
            { _client=client;_mutation=mutation;construction.Enter(); }
            public async Task<BackgroundJobExternalResult<Outcome>> ExecuteAsync(BackgroundJobExternalContext context,
                Plan plan,CancellationToken token=default) => Translate(
                    await _client.ExecuteAsync(context,plan.ResourceId,plan.FinalizedOrganizationId.Value,token));
            public async Task<BackgroundJobExternalResult<Outcome>> ReconcileAsync(BackgroundJobExternalContext context,
                Plan plan,CancellationToken token=default)
            {
                var report=await _client.ReconcileAsync(context,plan.ResourceId,plan.FinalizedOrganizationId.Value,token);
                // Deliberate protocol violation for qualification: it changes no
                // provider journal and affects Reconcile only, after restart.
                if(_mutation?.LieOnReconcile==true && report.State==BackgroundJobExternalState.Cancelled)
                {
                    _mutation.RecordApplied();
                    report=report with { State=BackgroundJobExternalState.Failed,Policy=BackgroundJobExternalFailurePolicy.Permanent };
                }
                return Translate(report);
            }
            private static BackgroundJobExternalResult<Outcome> Translate(BackgroundJobProviderReport report)
            {
                if(report.State==BackgroundJobExternalState.Running) return BackgroundJobExternalResult.Running<Outcome>();
                if(report.State==BackgroundJobExternalState.Unknown) return BackgroundJobExternalResult.Unknown<Outcome>();
                var outcome=new Outcome(report.OutcomeId ?? throw new InvalidOperationException("Provider outcome missing."));
                var evidence=new BackgroundJobQuiescenceEvidence(report.EvidenceId
                    ?? throw new InvalidOperationException("Provider fence missing."),BackgroundJobQuiescenceKind.ProviderFenced);
                return report.State switch
                {
                    BackgroundJobExternalState.Succeeded => BackgroundJobExternalResult.Succeeded(outcome,evidence),
                    BackgroundJobExternalState.Failed => BackgroundJobExternalResult.Failed(outcome,evidence,
                        report.Policy ?? throw new InvalidOperationException("Provider policy missing.")),
                    BackgroundJobExternalState.Cancelled => BackgroundJobExternalResult.Cancelled(outcome,evidence,
                        report.Policy ?? throw new InvalidOperationException("Provider policy missing.")),
                    _ => throw new InvalidOperationException("Unknown provider report."),
                };
            }
        }
        """;
}
