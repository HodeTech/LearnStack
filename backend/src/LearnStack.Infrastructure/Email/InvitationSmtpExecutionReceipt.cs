using System.Text.Json;
using LearnStack.Application.Jobs;
using LearnStack.Infrastructure.Jobs;
using LearnStack.SharedKernel.Jobs;

namespace LearnStack.Infrastructure.Email;

/// <summary>Invocation authority; never serialized or reconstructed from caller identifiers.</summary>
internal sealed class InvitationSmtpExecutionReceipt : IDisposable
{
    private static readonly AsyncLocal<InvitationSmtpExecutionReceipt?> Ambient = new();
    private readonly BackgroundJobRunGate _run;
    private readonly CancellationTokenRegistration _stop;
    private int _closed;
    internal static InvitationSmtpExecutionReceipt? Current => Ambient.Value;
    internal const string TenantDefinition = "identity.invitation_email_tenant.v1";
    internal const string OrganizationDefinition = "identity.invitation_email_organization.v1";
    internal const string CapacityGroup = "identity.invitation_email";

    internal InvitationSmtpExecutionReceipt(BackgroundJobEnvelope envelope, BackgroundJobDefinitionDescriptor definition,
        BackgroundJobExternalAdapterRegistration binding, string planJson, string fingerprint, Guid operation,
        ConfirmedStartReceipt? original, BackgroundJobExternalRecoveryBinding? recovery, CancellationToken token)
    {
        if (Ambient.Value is not null || (original is null) == (recovery is null)
            || envelope.DefinitionKey is not (TenantDefinition or OrganizationDefinition)
            || envelope.CapacityGroup != CapacityGroup || envelope.Queue != "external"
            || definition.ProfileRegistration.PlanType?.FullName != "LearnStack.Modules.Identity.Application.Contracts.Invitations.InvitationEmailPlan"
            || binding.PortType.FullName != "LearnStack.Modules.Identity.Application.Contracts.Invitations.IInvitationEmailOperation"
            || binding.ImplementationType.FullName != "LearnStack.Modules.Identity.Infrastructure.Invitations.InvitationSmtpOperation"
            || binding.PortType.Assembly.GetName().Name != "LearnStack.Modules.Identity.Application.Contracts"
            || binding.ImplementationType.Assembly.GetName().Name != "LearnStack.Modules.Identity.Infrastructure"
            || definition.ProfileRegistration.PlanType.Assembly != binding.PortType.Assembly)
            throw Refused();
        using var plan = JsonDocument.Parse(planJson);
        InvitationId = plan.RootElement.GetProperty(nameof(InvitationId)).GetGuid();
        GenerationId = plan.RootElement.GetProperty(nameof(GenerationId)).GetGuid();
        SpoolId = plan.RootElement.GetProperty(nameof(SpoolId)).GetGuid();
        JobId = envelope.JobId.Value;
        TenantId = envelope.TenantId.Value;
        OrganizationId = envelope.OrganizationId?.Value;
        Definition = envelope.DefinitionKey;
        AttemptId = original?.AttemptId.Value ?? recovery!.Receipt.AttemptId.Value;
        AdmissionToken = original?.Claim.Token ?? recovery!.Receipt.AdmissionToken;
        Deadline = original?.Deadline ?? recovery!.Receipt.Deadline;
        OperationId = operation;
        Fingerprint = fingerprint;
        RecoveryToken = recovery?.Receipt.RecoveryToken;
        WakeGeneration = original?.Claim.WakeGeneration ?? recovery!.Receipt.WakeGeneration;
        OwnershipUntil = original?.OwnershipUntil ?? recovery!.Receipt.OwnershipUntil;
        _run = original?.Run ?? recovery!.Run;
        if (_run.IsClosed || token.IsCancellationRequested) throw Refused();
        _stop = token.UnsafeRegister(static value => Interlocked.Exchange(ref ((InvitationSmtpExecutionReceipt)value!)._closed, 1), this);
        Ambient.Value = this;
    }

    internal Guid InvitationId { get; }
    internal Guid GenerationId { get; }
    internal Guid SpoolId { get; }
    internal Guid JobId { get; }
    internal Guid TenantId { get; }
    internal Guid? OrganizationId { get; }
    internal string Definition { get; }
    internal Guid AttemptId { get; }
    internal Guid AdmissionToken { get; }
    internal DateTimeOffset Deadline { get; }
    internal Guid OperationId { get; }
    internal string Fingerprint { get; }
    internal Guid? RecoveryToken { get; }
    internal long WakeGeneration { get; }
    internal DateTimeOffset OwnershipUntil { get; }
    internal bool IsRecovery => RecoveryToken is not null;
    internal bool IsActive => Volatile.Read(ref _closed) == 0 && !_run.IsClosed;

    internal void Require(Guid invitation, Guid generation, Guid spool, BackgroundJobExternalContext context, bool recovery)
    {
        if (!ReferenceEquals(Current, this) || !IsActive || !JobApplicationSourceOwner.IsExternal
            || invitation != InvitationId || generation != GenerationId || spool != SpoolId
            || context.JobId.Value != JobId || context.AttemptId.Value != AttemptId
            || context.OperationId != OperationId || context.Deadline != Deadline || recovery != IsRecovery) throw Refused();
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = null;
        _stop.Dispose();
    }
    internal static InvalidOperationException Refused() => new("Invitation SMTP invocation refused.");
}
