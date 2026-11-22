using FluentAssertions;
using LearnStack.Infrastructure.Jobs;
using LearnStack.Modules.Identity.Application.Contracts.DataRequests;
using LearnStack.SharedKernel.Jobs;
using Npgsql;
using static LearnStack.Tests.Integration.Database.IdentityMembershipFixture;

namespace LearnStack.Tests.Integration.Database;

/// <summary>
/// The pieces an ADR-0107 § 5 erasure proof needs beside <see cref="DataExportTestKit"/>: the clock seam that moves a
/// scheduled request's window into the past, and the erasure job row the runtime claims.
/// </summary>
internal static class DataErasureTestKit
{
    /// <summary>
    /// Moves a scheduled request's creation and execution time 25 hours into the past as the owner, with its guard paused
    /// and restored, so its window has ended by the database clock; the 24-hour window constraint still holds.
    /// </summary>
    /// <remarks>The owner is subject to the forced row policy, so it announces the request's own group.</remarks>
    internal static async Task BackdateAsync(string migrationConnectionString, Guid tenant, Guid? organization, Guid request)
    {
        await using var owner = (NpgsqlConnection)await PostgresFixture.OpenAsync(migrationConnectionString);
        await using var transaction = await owner.BeginTransactionAsync();
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests DISABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await ScopeAsync(owner, transaction, tenant, organization);
        (await ScalarAsync<long>(owner, transaction, """
            WITH moved AS (
                UPDATE public.data_subject_requests SET created_at=created_at - interval '25 hours', execute_at=execute_at - interval '25 hours'
                WHERE id=@id AND status='scheduled' RETURNING 1)
            SELECT count(*) FROM moved
            """, ("id", request))).Should().Be(1, "exactly the scheduled request's window moves");
        await ExecuteAsync(owner, transaction, "ALTER TABLE public.data_subject_requests ENABLE TRIGGER tg_data_subject_requests_mutation_guard");
        await transaction.CommitAsync();
    }

    /// <summary>The request's erasure job, as the scheduler would have stored it, due now.</summary>
    internal static Task<BackgroundJobId> SubmitAsync(DisposableSchemaDatabase database, Guid tenant, Guid? organization, Guid request) =>
        BackgroundJobRuntimeRows.InsertAsync(database.AppConnectionString,
            organization is null ? "identity.data_erasure_tenant.v1" : "identity.data_erasure_organization.v1",
            organization: organization, tenant: tenant, parameters: Parameters(request));

    internal static string Parameters(Guid request) => BackgroundJobPayloadContract.Serialize(new DataErasureJobParameters(request),
        typeof(DataErasureJobParameters), BackgroundJobPayloadKind.Parameters);
}
