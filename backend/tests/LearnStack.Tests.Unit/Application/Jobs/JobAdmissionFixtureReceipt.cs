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
        BackgroundJobExecutionStage? stage, BackgroundJobCommitGate? admissionGate = null,
        BackgroundJobRoutingClaim? claimOverride = null, bool bindRun = true,
        string? capacityGroup = null, bool acknowledgeAdmission = true)
    {
        using var table = new DataTable();
        foreach (var type in new[] { typeof(Guid), typeof(Guid), typeof(Guid), typeof(string), typeof(string),
                     typeof(long), typeof(Guid), typeof(DateTimeOffset), typeof(DateTimeOffset), typeof(string), typeof(string), typeof(DateTimeOffset) })
            table.Columns.Add(new DataColumn { DataType = type });
        var job = Guid.CreateVersion7();
        var due = new DateTimeOffset(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
        table.Rows.Add(job, tenantId == TenantId.PlatformSentinel ? Guid.CreateVersion7() : tenantId.Value, organizationId is { } organization ? organization.Value : DBNull.Value,
            definitionKey, "probe", 1L, Guid.NewGuid(), due.AddMinutes(1), due,
            stage == BackgroundJobExecutionStage.Transactional ? "transactional" : "external",
            stage == BackgroundJobExecutionStage.Finalize ? "finalize_pending" : "pending", due);
        using var reader = table.CreateDataReader();
        if (!reader.Read()) throw new InvalidOperationException("The test route row is missing.");
        var claim = claimOverride ?? MintClaim(reader);
        var envelope = new BackgroundJobEnvelope(claim.JobId, tenantId, organizationId, definitionKey,
            claim.Profile == BackgroundJobExecutionProfile.Transactional ? "transactional" : "external", "probe", capacityGroup,
            "{}", due, "00-11111111111111111111111111111111-1111111111111111-01", null, null, null, null);
        admissionGate ??= new BackgroundJobRunGate(new BackgroundJobRunGate.RuntimeStop())
            .OpenBoundary(BackgroundJobCheckpointKind.Admission);
        if (acknowledgeAdmission)
            foreach (var (field, value) in new[] { ("_state", 2), ("_issued", 1), ("_acknowledged", 1) })
                (typeof(BackgroundJobCommitGate).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("The synthetic admission field is unavailable.")).SetValue(admissionGate, value);
        var command = new AdmitBackgroundJobAttemptCommand(claim, envelope, admissionGate);
        var receipt = (ConfirmedStartReceipt)(Activator.CreateInstance(typeof(ConfirmedStartReceipt),
            BindingFlags.Instance | BindingFlags.NonPublic, null,
            [command, BackgroundJobAttemptId.From(Guid.CreateVersion7()), stage, 1,
                stage == BackgroundJobExecutionStage.Finalize ? 1 : 0,
                stage == BackgroundJobExecutionStage.Finalize ? BackgroundJobAttemptId.From(Guid.CreateVersion7()) : (BackgroundJobAttemptId?)null, due,
                due.AddMinutes(2), due.AddMinutes(1), JobApplicationSourceOwner.Create()], null)
            ?? throw new InvalidOperationException("The synthetic test receipt could not be constructed."));
        if (bindRun) receipt.BindRun(admissionGate.Run);
        return receipt;
    }
}
