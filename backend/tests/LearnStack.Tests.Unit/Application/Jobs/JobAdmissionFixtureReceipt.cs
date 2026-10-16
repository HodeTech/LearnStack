using System.Data;
using System.Reflection;
using LearnStack.Application.Jobs;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Tests.Unit.Application.Jobs;

/// <summary>
/// Synthetic authority for isolated ceiling tests. Only this test assembly uses reflection;
/// production receipts come exclusively from the acknowledged coordinator boundary.
/// </summary>
internal sealed class JobAdmissionFixtureReceipt : BackgroundJobRoutingClaim.MintingSource
{
    internal static ConfirmedStartReceipt Create(string definitionKey, TenantId tenantId, OrganizationId? organizationId,
        BackgroundJobExecutionStage stage)
    {
        using var table = new DataTable();
        foreach (var type in new[] { typeof(Guid), typeof(Guid), typeof(Guid), typeof(string), typeof(string),
                     typeof(long), typeof(Guid), typeof(DateTimeOffset), typeof(DateTimeOffset) })
            table.Columns.Add(new DataColumn { DataType = type });
        var job = Guid.CreateVersion7();
        var due = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        table.Rows.Add(job, tenantId == TenantId.PlatformSentinel ? Guid.CreateVersion7() : tenantId.Value, organizationId is { } organization ? organization.Value : DBNull.Value,
            definitionKey, "probe", 1L, Guid.NewGuid(), due.AddMinutes(1), due);
        using var reader = table.CreateDataReader();
        if (!reader.Read()) throw new InvalidOperationException("The test route row is missing.");
        var claim = MintClaim(reader);
        var envelope = new BackgroundJobEnvelope(BackgroundJobId.From(job), tenantId, organizationId, definitionKey,
            "transactional", "probe", null, "{}", due, "00-11111111111111111111111111111111-1111111111111111-01", null, null, null, null);
        var command = new AdmitBackgroundJobAttemptCommand(claim, envelope);
        return (ConfirmedStartReceipt)(Activator.CreateInstance(typeof(ConfirmedStartReceipt),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [command, BackgroundJobAttemptId.From(Guid.CreateVersion7()), stage], null)
            ?? throw new InvalidOperationException("The synthetic test receipt could not be constructed."));
    }
}
