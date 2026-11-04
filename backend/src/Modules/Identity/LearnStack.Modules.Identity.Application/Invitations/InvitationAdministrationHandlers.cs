using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Errors;
using LearnStack.Modules.Identity.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Invitations;
using LearnStack.Modules.Identity.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.IdentityLifecycle;
using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.Email;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Pagination;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Identity.Application.Invitations;

public sealed class InvitationAdministrationHandlers(IInvitationAdministrationStore store, IRoleReadStore roles,
    InvitationAdministrationAuthorization authorization, ITenantContext context, IIdentityMutationLocks locks,
    IIdentityScopeLifecycleReader lifecycle, ITenantSettingsAccessor settings, IInvitationMessageContextReader messageContext, IInvitationEntryOriginPolicy origins,
    IInvitationEmailStager stager, IInvitationEmailStatusReader status, IAuditSubject subject, IGuidFactory ids, IClock clock, IUnitOfWork unit)
    : IRequestHandler<CreateInvitationCommand, Result<InvitationMutationResult>>,
      IRequestHandler<RevokeInvitationCommand, Result<InvitationMutationResult>>,
      IRequestHandler<ReissueInvitationCommand, Result<InvitationMutationResult>>,
      IRequestHandler<ListInvitationsQuery, Result<Page<InvitationDto>>>, IRequestHandler<GetInvitationQuery, Result<InvitationDto>>,
      IRequestHandler<ListInvitableRolesQuery, Result<InvitableRoleDirectory>>, IRequestHandler<ReadInvitationReceiptQuery, Result<None>>
{
    public Task<Result<InvitationMutationResult>> Handle(CreateInvitationCommand request, CancellationToken cancellationToken) => MutateAsync(request, cancellationToken);
    public Task<Result<InvitationMutationResult>> Handle(RevokeInvitationCommand request, CancellationToken cancellationToken) => MutateAsync(request, cancellationToken);
    public Task<Result<InvitationMutationResult>> Handle(ReissueInvitationCommand request, CancellationToken cancellationToken) => MutateAsync(request, cancellationToken);
    private async Task<Result<InvitationMutationResult>> MutateAsync(object request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<InvitationMutationResult>.Fail(admission.Error);
        var create = request as CreateInvitationCommand;
        var targetId = request switch { RevokeInvitationCommand value => value.InvitationId, ReissueInvitationCommand value => value.InvitationId, _ => Guid.Empty };
        var expected = request switch { RevokeInvitationCommand value => value.ExpectedVersion, ReissueInvitationCommand value => value.ExpectedVersion, _ => 0 };
        if (create is not null ? !ValidId(create.RoleId) || create.ExpectedRoleVersion < 0 || !InvitationMailbox.TryNormalize(create.Email, out _)
            : !ValidId(targetId) || expected < 0) return Fail<InvitationMutationResult>("lockey_validation_failed");
        try
        {
            var before = create is null ? await store.ReadAsync(InvitationId.From(targetId), cancellationToken) : null;
            if (create is null && before is null) return Fail<InvitationMutationResult>("lockey_not_found");
            var roleId = create is null ? before!.RoleId : RoleId.From(create.RoleId);
            var roleVersion = create?.ExpectedRoleVersion ?? before!.PinnedRoleVersion;
            var plan = new IdentityMutationLockPlan([], context.TenantId, IdentityTenantLockMode.Shared);
            await locks.AcquireAsync(plan, cancellationToken);
            var withdrawal = request is RevokeInvitationCommand;
            if (!withdrawal) await roles.LockAsync(roleId, cancellationToken);
            var role = withdrawal ? null : await roles.ReadAsync(roleId, cancellationToken);
            var target = create is null ? await store.FindLockedAsync(InvitationId.From(targetId), cancellationToken) : null;
            var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
            if (check.IsFailure) return Result<InvitationMutationResult>.Fail(check.Error);
            if (!await AvailableAsync(cancellationToken)) return Fail<InvitationMutationResult>("lockey_not_found");
            if (!withdrawal && role is null) return Fail<InvitationMutationResult>("lockey_not_found");
            if (!withdrawal && role!.Version != roleVersion) return Fail<InvitationMutationResult>("lockey_concurrency_conflict");
            if (!withdrawal)
            {
                var delegation = await authorization.CanDelegateAsync(request, admission.Value, role!, cancellationToken);
                if (delegation.IsFailure) return Result<InvitationMutationResult>.Fail(delegation.Error);
                if (!delegation.Value) return Fail<InvitationMutationResult>("lockey_forbidden");
            }
            if (create is null && (target is null || target.TenantId != context.TenantId || target.OrganizationId != context.OrganizationId)) return Fail<InvitationMutationResult>("lockey_not_found");
            if (target is not null && (target.Version != expected || target.RoleId != roleId || target.PinnedRoleVersion != roleVersion
                || target.Status != InvitationStatus.Pending)) return Fail<InvitationMutationResult>("lockey_concurrency_conflict");
            locks.RequireCurrent(plan);
            if (request is RevokeInvitationCommand)
            {
                subject.Designate(target!);
                var revoked = target!.Revoke(clock.UtcNow, context.UserId!.Value);
                if (revoked.IsFailure) return Result<InvitationMutationResult>.Fail(revoked.Error);
                await store.UpdateAsync(target, cancellationToken);
            }
            else
            {
                var policy = await settings.ReadAsync(TenantSettingKeys.InvitationLifetime, cancellationToken);
                if (policy.IsFailure) return Result<InvitationMutationResult>.Fail(policy.Error);
                var deliveryContext = await messageContext.ReadAsync(cancellationToken);
                if (deliveryContext.IsFailure) return Result<InvitationMutationResult>.Fail(deliveryContext.Error);
                var id = target?.Id ?? InvitationId.From(ids.NewUuidV7());
                var delivery = await stager.StageAsync(id, InvitationGenerationId.From(ids.NewUuidV7()), create?.Email ?? target!.RecipientEmail,
                    origins.ForHost(deliveryContext.Value.EntryHost), deliveryContext.Value.Locale, deliveryContext.Value.TenantLabel,
                    policy.Value.Value?.Days ?? InvitationLifetimePolicy.DefaultDays, cancellationToken);
                // Staging qualifies server diagnostic privacy before the first recipient-bearing root INSERT.
                if (create is not null)
                {
                    var created = Invitation.Create(id, context.TenantId, context.OrganizationId, create.Email, role!, delivery, clock.UtcNow, context.UserId!.Value);
                    if (created.IsFailure) return Result<InvitationMutationResult>.Fail(created.Error);
                    target = created.Value; subject.Designate(target);
                    await store.AddAsync(target, cancellationToken);
                }
                else
                {
                    subject.Designate(target!);
                    var reissued = target!.Reissue(role!, delivery, clock.UtcNow, context.UserId!.Value);
                    if (reissued.IsFailure) return Result<InvitationMutationResult>.Fail(reissued.Error);
                    await store.UpdateAsync(target, cancellationToken);
                }
            }
            return Result.Ok(new InvitationMutationResult(target!.Id.Value, target.Version));
        }
        catch (IdentityFoundationStorageException failure)
        {
            return Fail<InvitationMutationResult>(failure.Failure == IdentityFoundationStorageFailure.Conflict ? "lockey_concurrency_conflict" : "lockey_dependency_unavailable");
        }
        catch (IdentityMutationLockException) { return Fail<InvitationMutationResult>("lockey_dependency_unavailable"); }
    }
    public async Task<Result<Page<InvitationDto>>> Handle(ListInvitationsQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<Page<InvitationDto>>.Fail(admission.Error);
        var scope = InvitationDirectoryCursorCodec.Scope(context.UserId!.Value, context.TenantId, context.OrganizationId);
        InvitationId? after = null;
        if (request.Limit is < 1 or > 100 || request.Cursor is not null && !InvitationDirectoryCursorCodec.TryDecode(request.Cursor, scope, out after))
            return Fail<Page<InvitationDto>>("lockey_validation_failed");
        var rows = await store.ListAsync(after, request.Limit + 1, cancellationToken);
        var items = new List<InvitationDto>();
        using var pageBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        pageBudget.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            foreach (var row in rows.Take(request.Limit)) items.Add(await ProjectAsync(row, pageBudget.Token));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && pageBudget.IsCancellationRequested)
        { unit.MarkRollbackOnly(); throw new InfrastructureException("Invitation page delivery status unavailable."); }
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return Result<Page<InvitationDto>>.Fail(check.Error);
        var next = rows.Count > request.Limit ? InvitationDirectoryCursorCodec.Encode(scope, rows[request.Limit - 1].Id) : null;
        return Result.Ok(new Page<InvitationDto>(items, new PageInfo(next, null, next is not null, false)));
    }
    public async Task<Result<InvitationDto>> Handle(GetInvitationQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<InvitationDto>.Fail(admission.Error);
        if (!ValidId(request.InvitationId)) return Fail<InvitationDto>("lockey_validation_failed");
        var target = await store.ReadAsync(InvitationId.From(request.InvitationId), cancellationToken);
        if (target is null) return Fail<InvitationDto>("lockey_not_found");
        var result = await ProjectAsync(target, cancellationToken);
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return Result<InvitationDto>.Fail(check.Error);
        subject.Designate(target); return Result.Ok(result);
    }
    public async Task<Result<InvitableRoleDirectory>> Handle(ListInvitableRolesQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<InvitableRoleDirectory>.Fail(admission.Error);
        var graph = await store.ReadRolesAsync(cancellationToken);
        if (graph.Count > IdentityFoundationManifest.Version1.Templates.Length) return Fail<InvitableRoleDirectory>("lockey_dependency_unavailable");
        var result = new List<InvitableRoleDto>();
        foreach (var role in graph)
        {
            var delegation = await authorization.CanDelegateAsync(request, admission.Value, role, cancellationToken);
            if (delegation.IsFailure) return Result<InvitableRoleDirectory>.Fail(delegation.Error);
            if (delegation.Value) result.Add(new(role.Id.Value, role.BuiltInKey.ToString(), role.PermissionScope.ToString(), role.Version));
        }
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        return check.IsFailure ? Result<InvitableRoleDirectory>.Fail(check.Error) : Result.Ok(new InvitableRoleDirectory(result));
    }
    public async Task<Result<None>> Handle(ReadInvitationReceiptQuery request, CancellationToken cancellationToken)
    {
        var admission = await authorization.AdmitAsync(request, cancellationToken);
        if (admission.IsFailure) return Result<None>.Fail(admission.Error);
        if (!ValidId(request.InvitationId)) return Fail<None>("lockey_validation_failed");
        var target = await store.ReadAsync(InvitationId.From(request.InvitationId), cancellationToken);
        var check = await authorization.RecheckAsync(request, admission.Value, cancellationToken);
        if (check.IsFailure) return check;
        if (target is null) return Fail<None>("lockey_not_found");
        subject.Designate(target); return Result.Ok(None.Value);
    }
    private async Task<InvitationDto> ProjectAsync(Invitation target, CancellationToken cancellationToken) => new(target.Id.Value,
        target.RecipientEmail, target.OrganizationId, target.RoleId.Value, target.PinnedRoleVersion,
        target.IsExpired(clock.UtcNow) ? "Expired" : target.Status.ToString(), target.ExpiresAt, target.Version,
        (await status.ReadAsync(target.CurrentDeliveryJobId, cancellationToken)).ToString(),
        target.AcceptanceDisposition switch
        {
            InvitationAcceptanceDisposition.Created => "created",
            InvitationAcceptanceDisposition.AlreadyMember => "already_member",
            _ => null
        });
    private async Task<bool> AvailableAsync(CancellationToken cancellationToken)
    {
        var current = await lifecycle.ReadAsync(context.TenantId, context.OrganizationId, cancellationToken);
        return current.TenantExistsAndNotDeleted && current.TenantAllowsOrdinaryWork
            && (context.OrganizationId is null || current.OrganizationBelongsToTenant && current.OrganizationAllowsOrdinaryWork);
    }
    private static bool ValidId(Guid id) => id != Guid.Empty && id.Version == 7;
    private static Result<T> Fail<T>(string key) => InvitationAdministrationAuthorization.Fail<T>(key);
}
