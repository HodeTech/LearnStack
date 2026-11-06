using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Idempotency;
using LearnStack.SharedKernel.Results;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Contracts.Settings;

public sealed record InvitationPolicyDto(int Days, bool Present, long? Version, bool CanEdit);
public sealed record InvitationPolicyMutationResult(Guid SettingId, long Version);
public sealed record GetTenantInvitationPolicyQuery() : IRequest<Result<InvitationPolicyDto>>;
public sealed record SetTenantInvitationLifetimeCommand(int Days, bool ExpectedPresent, long? ExpectedVersion)
    : IRequest<Result<InvitationPolicyMutationResult>>;
public sealed record ReadInvitationPolicyReceiptQuery(Guid SettingId) : IRequest<Result<None>>, IIdempotencyReceiptRead;

/// <summary>Frozen server-owned message facts for the current announced scope; no scope or URL selector.</summary>
public interface IInvitationMessageContextReader
{
    Task<Result<InvitationMessageContext>> ReadAsync(CancellationToken cancellationToken = default);
    Task<Result<InvitationMessageContext>> ReadEntryAsync(string admittedHost, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<InvitationMessageContext>.Fail(new Error(LearnStack.SharedKernel.Localization.LocalizedMessage.Of("lockey_not_found"))));
}
public sealed record InvitationMessageContext(string EntryHost, string Locale, [property: PiiSensitive] string TenantLabel, [property: PiiSensitive] string? OrganizationLabel = null)
{
    public override string ToString() => nameof(InvitationMessageContext);
}

/// <summary>Server composition owns the fixed HTTPS port policy; the caller supplies a proven stored host.</summary>
public interface IInvitationEntryOriginPolicy
{
    string ForHost(string normalizedHost);
}
