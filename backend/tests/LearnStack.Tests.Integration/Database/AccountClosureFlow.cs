using System.Diagnostics;
using FluentAssertions;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Jobs;
using LearnStack.SharedKernel.Platform;
using LearnStack.Tests.Fixtures;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// A whole closure through the production composition: the API host over a disposable database with the operator's grant
/// held, the real gate, platform scope, audit store, redaction executor, fan-out, operator act executor, erasure job and
/// runtime, and an identity provider stand-in in place of the account-removal adapter. Begin and Finalize are resolved from a
/// request scope with the trace an HTTP request carries, as their route resolves them.
/// </summary>
internal static class AccountClosureFlow
{
    internal static WebApplicationFactory<Program> Host(DisposableSchemaDatabase database, IIdentityProviderAccountRemoval removal) =>
        new ConstructionOnlyApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(Environments.Development);
            builder.UseSetting("ConnectionStrings:Default", database.AppConnectionString);
            builder.UseSetting("ConnectionStrings:OutboxDispatcher", database.OutboxConnectionString);
            builder.UseSetting("ConnectionStrings:PlatformAdmin", database.PlatformConnectionString);
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IPlatformOperatorGrantSource>();
                services.AddSingleton<IPlatformOperatorGrantSource>(new Grants());
                services.RemoveAll<IIdentityProviderAccountRemoval>();
                services.AddSingleton(removal);
            });
        });

    internal static PlatformAdmission Admission(PlatformOperation operation, string? ticket) => TestPlatformAdmissions.Admission(
        operation, ticket, correlationId: "00-closure-flow-" + Guid.NewGuid().ToString("N"));

    internal static async Task<ClosureBeginResult> BeginAsync(WebApplicationFactory<Program> host, Guid closure, string? ticket = null)
    {
        using var activity = new Activity("closure-flow-begin").Start();
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountClosureOperatorOperations>().BeginAsync(
            Admission(PlatformOperations.ClosureBegin, ticket), closure);
    }

    internal static async Task<ClosureFinalizeResult> FinalizeAsync(WebApplicationFactory<Program> host, PlatformAdmission admission, Guid closure)
    {
        using var activity = new Activity("closure-flow-finalize").Start();
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IAccountClosureOperatorOperations>().FinalizeAsync(admission, closure);
    }

    /// <summary>The group's one fan-out erasure of the person, read under the group's own announcement.</summary>
    internal static async Task<Guid> FanOutRequestAsync(DisposableSchemaDatabase database, ClosureParticipantGroup group, Guid person)
    {
        await using var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString);
        await using var transaction = await app.BeginTransactionAsync();
        await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
        return await ScalarAsync<Guid>(app, transaction, """
            SELECT id FROM public.data_subject_requests
            WHERE subject_user_id = @subject AND tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization::uuid
              AND kind = 'erasure' AND origin = 'closure_fanout'
            """, ("subject", person), ("tenant", group.TenantId.Value), ("organization", (object?)group.OrganizationId?.Value ?? DBNull.Value));
    }

    /// <summary>Runs the group's erasure job for the request through the actual runtime, as the job host would.</summary>
    internal static async Task RunErasureJobAsync(
        WebApplicationFactory<Program> host, DisposableSchemaDatabase database, ClosureParticipantGroup group, Guid request)
    {
        Guid job;
        await using (var app = await DataExportTestKit.OpenAppAsync(database.AppConnectionString))
        await using (var transaction = await app.BeginTransactionAsync())
        {
            await ScopeAsync(app, transaction, group.TenantId.Value, group.OrganizationId?.Value);
            job = await ScalarAsync<Guid>(app, transaction, """
                SELECT id FROM public.background_jobs
                WHERE tenant_id = @tenant AND organization_id IS NOT DISTINCT FROM @organization::uuid AND definition_key = @definition
                  AND parameters = @parameters
                ORDER BY created_at DESC, id DESC LIMIT 1
                """, ("tenant", group.TenantId.Value), ("organization", (object?)group.OrganizationId?.Value ?? DBNull.Value),
                ("definition", group.OrganizationId is null ? "identity.data_erasure_tenant.v1" : "identity.data_erasure_organization.v1"),
                ("parameters", DataErasureTestKit.Parameters(request)));
        }

        var executor = host.Services.GetRequiredService<JobExecutor>();
        var start = await executor.ExecuteAsync(
            await DataExportTestKit.RoutingClaim.ClaimAsync(database.OutboxConnectionString, BackgroundJobId.From(job)), BackgroundJobTestRun.Create());
        start.Outcome.Should().Be(JobStartExecutionOutcome.Started);
        (await executor.ExecuteBusinessAsync(start.Receipt!, start.Receipt!.Run.OpenBoundary(BackgroundJobCheckpointKind.Transactional)))
            .Committed();
    }

    /// <summary>The operator's grant of the one key every closure act and every fan-out act needs.</summary>
    private sealed class Grants : IPlatformOperatorGrantSource
    {
        public ValueTask<bool> IsGrantedAsync(string subjectDigest, string permissionKey, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(permissionKey == PlatformPermissions.DataRequestWrite.Key
                && subjectDigest == TestPlatformAdmissions.Witness().SubjectDigest);
    }
}
