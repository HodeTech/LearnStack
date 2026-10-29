using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Authorization;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;

namespace LearnStack.Modules.Tenancy.Application.Administration;

/// <summary>Organization-only writes; Tenant metadata is detached read authority.</summary>
public abstract class OrganizationAdministrationCommandHandlerBase(IOrganizationWriteStore store,
    ITenancyAdministrationReader reader, TenancyAdministrationAuthorization authorization, ITenantContext context,
    IAuditSubject subject, IClock clock, IGuidFactory guids)
{
    protected async Task<Result<OrganizationMutationResult>> ExecuteAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var admission = await authorization.AdmitWriteAsync(request, cancellationToken);
            if (admission.IsFailure) return Result<OrganizationMutationResult>.Fail(admission.Error);
            var id = request switch
            {
                CreateManagedOrganizationCommand => OrganizationId.From(guids.NewUuidV7()),
                RenameOrganizationCommand rename => rename.OrganizationId,
                ArchiveOrganizationCommand archive => archive.OrganizationId,
                _ => throw new ArgumentException("Organization administration request is invalid.", nameof(request)),
            };
            var target = await store.FindForUpdateAsync(id, cancellationToken);
            var check = await authorization.VerifyWriteAsync(admission.Value, cancellationToken);
            if (check.IsFailure) return Result<OrganizationMutationResult>.Fail(check.Error);
            if (request is CreateManagedOrganizationCommand)
            {
                if (target is not null) return Fail("lockey_concurrency_conflict");
            }
            else
            {
                if (target is null || target.TenantId != context.TenantId || target.IsDeleted || target.DeletedBy is not null)
                    return Fail("lockey_not_found");
                subject.Designate(target);
                var expected = request is RenameOrganizationCommand rename ? rename.ExpectedVersion
                    : ((ArchiveOrganizationCommand)request).ExpectedVersion;
                if (target.Version != expected) return Fail("lockey_concurrency_conflict");
                if (request is RenameOrganizationCommand && target.Status == OrganizationStatus.Archived)
                    return Fail("lockey_business_rule_violation");
                if (request is ArchiveOrganizationCommand)
                {
                    var tenant = await reader.ReadTenantAsync(cancellationToken);
                    var otherActive = await reader.HasOtherActiveOrganizationAsync(target.Id, cancellationToken);
                    check = await authorization.VerifyWriteAsync(admission.Value, cancellationToken);
                    if (check.IsFailure) return Result<OrganizationMutationResult>.Fail(check.Error);
                    if (tenant is null || tenant.Id != context.TenantId || tenant.IsDeleted) return Fail("lockey_not_found");
                    if (tenant.DefaultOrganizationId == target.Id || !otherActive)
                        return Fail("lockey_business_rule_violation");
                }
            }
            if (request is CreateManagedOrganizationCommand create)
            {
                target = Organization.Create(id, context.TenantId, create.Slug, create.DisplayName, clock, context.UserId!.Value);
                subject.Designate(target);
                await store.AddAsync(target, cancellationToken);
            }
            else if (request is RenameOrganizationCommand rename && target!.DisplayName != rename.DisplayName)
            {
                target.Rename(rename.DisplayName, clock, context.UserId!.Value);
                await store.UpdateAsync(target, cancellationToken);
            }
            else if (request is ArchiveOrganizationCommand && target!.Status != OrganizationStatus.Archived)
            {
                target.ChangeStatus(OrganizationStatus.Archived, clock, context.UserId!.Value);
                await store.UpdateAsync(target, cancellationToken);
            }
            return Result.Ok(new OrganizationMutationResult(target!.Id, target.Version));
        }
        catch (Exception failure) when (failure is TenancyAdministrationStorageException or IdentityMutationLockException
            or AggregateConcurrencyException or AggregateConflictException)
        {
            return authorization.StorageFailure<OrganizationMutationResult>(failure);
        }
    }

    private static Result<OrganizationMutationResult> Fail(string key) => TenancyAdministrationFailures.Code<OrganizationMutationResult>(key);
}
