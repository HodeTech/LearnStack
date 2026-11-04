using LearnStack.Modules.Tenancy.Domain;

namespace LearnStack.Modules.Tenancy.Application.Settings;

public interface IInvitationPolicyReader
{
    Task<TenantSetting?> ReadAsync(CancellationToken cancellationToken = default);
    Task<TenantSetting?> ReadReceiptAsync(TenantSettingId id, CancellationToken cancellationToken = default);
}
