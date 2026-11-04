using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Administration;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Branding;

public sealed class SetManagedTenantBrandingCommandHandler(ITenantSettingWriteStore store, ITenancyAdministrationReader reader,
    TenancyAdministrationAuthorization authorization, ITenantContext context, IAuditSubject subject, IClock clock, IGuidFactory guids)
    : IRequestHandler<SetManagedTenantBrandingCommand, Result<long>>
{
    public async Task<Result<long>> Handle(SetManagedTenantBrandingCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var admission = await authorization.AdmitWriteAsync(request, cancellationToken);
            if (admission.IsFailure) return Result<long>.Fail(admission.Error);
            var theme = BrandingThemeRegistry.ValidateAndCanonicalize(request.Theme);
            if (theme.IsFailure) return Result<long>.Fail(theme.Error);
            var current = await reader.ReadBrandingAsync(cancellationToken);
            var id = current?.Id ?? TenantSettingId.From(guids.NewUuidV7());
            var target = await store.FindForUpdateAsync(id, cancellationToken);
            var check = await authorization.VerifyWriteAsync(admission.Value, cancellationToken);
            if (check.IsFailure) return Result<long>.Fail(check.Error);
            if (target is not null && (target.TenantId != context.TenantId || target.OrganizationId is not null
                || target.Key != BrandingThemeRegistry.SettingKey || target.IsDeleted || target.DeletedBy is not null))
                return Fail("lockey_not_found");
            if (target is not null) subject.Designate(target);
            if (request.ExpectedVersion is null)
            {
                if (current is not null || target is not null) return Fail("lockey_concurrency_conflict");
                target = TenantSetting.Create(id, context.TenantId, null, BrandingThemeRegistry.SettingKey,
                    theme.Value, clock, context.UserId!.Value);
                subject.Designate(target);
                await store.AddAsync(target, cancellationToken);
            }
            else
            {
                if (current is null || target is null) return Fail("lockey_not_found");
                if (target.Version != request.ExpectedVersion.Value) return Fail("lockey_concurrency_conflict");
                var previous = BrandingThemeRegistry.ValidateAndCanonicalize(target.Value);
                if (previous.IsFailure || previous.Value != theme.Value)
                {
                    target.SetValue(theme.Value, clock, context.UserId!.Value);
                    await store.UpdateAsync(target, cancellationToken);
                }
            }
            return Result.Ok(target.Version);
        }
        catch (Exception failure) when (failure is TenancyAdministrationStorageException or IdentityMutationLockException
            or AggregateConcurrencyException or AggregateConflictException)
        {
            return authorization.StorageFailure<long>(failure);
        }
    }

    private static Result<long> Fail(string key) => TenancyAdministrationFailures.Code<long>(key);
}
