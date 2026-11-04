using FluentValidation;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Seeding;

/// <summary>One Tenant root and its fixed host projection, committed with central plural MUST audit.</summary>
internal sealed class AdoptSeedAdministrationOriginCommandHandler(
    ITenantWriteStore tenants,
    IPlatformHostMappingStore mappings,
    ISeedStateReader seedState,
    ISeedAdministrationOriginAdmission admission,
    IFixedSeedOriginCompletionVerifier completion,
    IIdentityMutationLocks locks,
    IOperationInvocationContext execution,
    ITenantContext context,
    IUnitOfWork unitOfWork,
    IAuditSubject auditSubject,
    IReservedHostRegistry reservedHosts,
    IHostResolutionInvalidator resolutionCache,
    IClock clock) : IRequestHandler<AdoptSeedAdministrationOriginCommand, Result<long>>
{
    public async Task<Result<long>> Handle(AdoptSeedAdministrationOriginCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!HasCurrentSeedInvocation(request) || !context.IsResolved || context.OrganizationId is not null
            || context.Origin != TenantContextOrigin.Ambient || context.HostScope is not null
            || context.UserId is { } actor && actor != UserId.SystemActor
            || SeedAdministrationOriginManifest.Find(context.TenantId) is not { } declaration
            || admission.ForTenant(context.TenantId) != declaration || reservedHosts.IsReserved(declaration.Host))
            return Refused();

        var plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Exclusive);
        await locks.AcquireAsync(plan, cancellationToken);
        if (!HasCurrentSeedInvocation(request) || admission.ForTenant(context.TenantId) != declaration)
            return Refused();
        var tenant = await tenants.FindForUpdateAsync(context.TenantId, cancellationToken);
        if (!HasCurrentSeedInvocation(request) || admission.ForTenant(context.TenantId) != declaration
            || tenant is null || tenant.Id != declaration.TenantId || tenant.Slug != declaration.TenantSlug
            || tenant.CreatedAt == default || tenant.CreatedBy != UserId.SystemActor
            || tenant.DeletedAt is not null || tenant.DeletedBy is not null || tenant.Version < 1
            || tenant.Status is not (TenantStatus.Trial or TenantStatus.Active or TenantStatus.Suspended)
            || !tenant.HasValidLocaleConfiguration() || tenant.DefaultOrganizationId is not { } defaultOrganization)
            return Refused();

        auditSubject.Designate(tenant);
        if (tenant.Version != request.ExpectedVersion)
            return TenantWriteFailures.Code<long>("lockey_concurrency_conflict");
        // Completed commands remain ordinary refusals. The CLI repeat stops
        // before dispatch, without declaring this operation's MUST intents.
        if (tenant.AdoptedSeedOriginVersion is not (0 or 1)) return Refused();

        var organization = await seedState.ReadOrganizationAsync(defaultOrganization, cancellationToken);
        locks.RequireCurrent(plan);
        if (!HasCurrentSeedInvocation(request) || admission.ForTenant(context.TenantId) != declaration
            || organization is null || organization.Id != defaultOrganization || organization.TenantId != tenant.Id
            || organization.Status != nameof(OrganizationStatus.Active)
            || organization.DeletedAt is not null || organization.DeletedBy is not null)
            return Refused();

        var mapping = await mappings.FindForUpdateAsync(declaration.Host, cancellationToken);
        if (mapping is not null && !IsExact(mapping, declaration)) return Refused();
        if (tenant.AdoptedSeedOriginVersion == 1 && mapping is null) return Refused();

        locks.RequireCurrent(plan);
        if (!HasCurrentSeedInvocation(request) || admission.ForTenant(context.TenantId) != declaration)
            return Refused();

        var prepared = new SeedAdministrationOriginPreparedTarget(tenant.Id, tenant.Slug,
            tenant.Version, tenant.AdoptedSeedOriginVersion, tenant.DefaultOrganizationId,
            mapping is null ? null : new SeedAdministrationOriginPreparedMapping(mapping.Host,
                mapping.TenantId, mapping.OrganizationId, mapping.IsActive, mapping.IsPubliclyLive));
        if (!await completion.VerifyAsync(request, prepared, plan, cancellationToken)) return Refused();
        // The closed verifier returns only after acknowledging narrow restoration.
        locks.RequireCurrent(plan);
        if (!HasCurrentSeedInvocation(request) || admission.ForTenant(context.TenantId) != declaration
            || tenant.Version != prepared.TenantVersion || tenant.AdoptedSeedOriginVersion != prepared.Marker)
            return Refused();

        var insert = mapping is null;
        mapping ??= PlatformHostMapping.Create(declaration.Host, tenant.Id,
            organizationId: null, isActive: true, isPubliclyLive: true);
        auditSubject.DesignateProjection(mapping);
        try
        {
            // The INSERT/locked existing projection precedes the marker UPDATE. Its
            // invoker guard independently locks and verifies that exact live row.
            if (insert) await mappings.AddAsync(mapping, cancellationToken);
            tenant.AdoptSeedAdministrationOrigin(clock, UserId.SystemActor);
            await tenants.UpdateAsync(tenant, cancellationToken);
        }
        catch (AggregateConflictException conflict) when (conflict.ConstraintName == "pk_platform_host_to_tenant")
        {
            unitOfWork.MarkRollbackOnly();
            return Refused();
        }
        catch (AggregateConcurrencyException)
        {
            unitOfWork.MarkRollbackOnly();
            return TenantWriteFailures.Code<long>("lockey_concurrency_conflict");
        }

        if (insert) await resolutionCache.InvalidateAsync(declaration.Host, cancellationToken);
        return Result.Ok(tenant.Version);
    }

    private bool HasCurrentSeedInvocation(AdoptSeedAdministrationOriginCommand request) =>
        execution.CurrentInvocation is { IsActive: true, EntryKind: OperationEntryKind.Application } invocation
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && invocation.Policy.RequestType == typeof(AdoptSeedAdministrationOriginCommand)
        && invocation.Policy.Kind == OperationPolicyKind.FiniteTrusted
        && invocation.Policy.AllowedSources.Length == 1
        && invocation.Policy.AllowedSources[0] == AuthorizationSourceKind.Seed
        && invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.Seed } source
        && source.Matches(typeof(AdoptSeedAdministrationOriginCommand), context);

    private static bool IsExact(PlatformHostMapping mapping, SeedAdministrationOriginDeclaration declaration) =>
        mapping.Host == declaration.Host && mapping.TenantId == declaration.TenantId
        && mapping.OrganizationId is null && mapping.IsActive && mapping.IsPubliclyLive;

    private static Result<long> Refused() => TenantWriteFailures.Code<long>("lockey_business_rule_violation");
}

internal sealed class AdoptSeedAdministrationOriginCommandValidator : AbstractValidator<AdoptSeedAdministrationOriginCommand>
{
    public AdoptSeedAdministrationOriginCommandValidator() =>
        RuleFor(command => command.ExpectedVersion).GreaterThanOrEqualTo(0)
            .WithErrorCode("lockey_validation_failed");
}
