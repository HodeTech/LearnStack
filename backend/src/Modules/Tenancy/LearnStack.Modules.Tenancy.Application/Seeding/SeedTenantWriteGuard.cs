using LearnStack.Modules.Customization.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Identity.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Application.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Permissions;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;

namespace LearnStack.Modules.Tenancy.Application.Seeding;

/// <summary>Exact v1 continuation on the caller's existing owner; registration grants no repair authority.</summary>
public sealed class SeedTenantWriteGuard(ISeedTenantContinuationSource declarations, ISeedStateReader state,
    IIdentityMutationLocks locks, IOperationInvocationContext execution, ITenantContext context, IUnitOfWork unit,
    IFixedSeedPrerequisiteVerifier prerequisites, ISeedCustomizationPrerequisiteReader customization)
    : ISeedTenantContinuationGuard
{
    internal async Task<Result<SeedTenantWriteAdmission>> AdmitAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invocation = execution.CurrentInvocation;
        var act = Describe(request);
        if (act is null || !HasInvocation(invocation, request) || !HasScope(act)
            || declarations.GetDeclaration(act) is not { } declaration || declaration.TenantId != act.TenantId
            || !MatchesRequest(request, declaration))
            return Refused<SeedTenantWriteAdmission>();
        var plan = new IdentityMutationLockPlan([], act.TenantId, IdentityTenantLockMode.Exclusive);
        var admission = new SeedTenantWriteAdmission(invocation!, request, act, declaration, plan);
        try
        {
            // Closed declaration and exact source admission precede the first lock or read.
            await locks.AcquireAsync(plan, cancellationToken);
            if (RequireCurrent(admission, request) is { IsFailure: true } failure)
                return Result<SeedTenantWriteAdmission>.Fail(failure.Error);
            // The caller prepares its target under this one plan before the authoritative read.
            // Provisioning remains INSERT-first and never calls VerifyAsync.
            return Result.Ok(admission);
        }
        catch (IdentityMutationLockException)
        {
            unit.MarkRollbackOnly();
            return TenantWriteFailures.Code<SeedTenantWriteAdmission>("lockey_dependency_unavailable");
        }
    }

    internal async Task<Result<None>> VerifyAsync(SeedTenantWriteAdmission admission, object request,
        CancellationToken cancellationToken)
    {
        if (RequireCurrent(admission, request) is { IsFailure: true } changed) return changed;
        if (admission.Act.Operation == SeedTenantContinuationOperation.ProvisionTenant) return Refused<None>();
        try
        {
            if (!await IsPristineStageAsync(admission, cancellationToken))
            {
                if (RequireCurrent(admission, request) is { IsFailure: true } expired) return expired;
                // Another fixed writer may have completed this act while the plan
                // waited. The runner may verify that postcondition once; this
                // refusal itself grants no retry, repair or next-stage admission.
                return TenantWriteFailures.Code<None>("lockey_concurrency_conflict");
            }
            if (RequireCurrent(admission, request) is { IsFailure: true } before) return before;
            if (!await prerequisites.VerifyAsync(request, admission.Plan, cancellationToken)) return Refused<None>();
            return RequireCurrent(admission, request);
        }
        catch (IdentityMutationLockException)
        {
            unit.MarkRollbackOnly();
            return TenantWriteFailures.Code<None>("lockey_dependency_unavailable");
        }
    }

    internal Result<None> RequireCurrent(SeedTenantWriteAdmission admission, object request)
    {
        ArgumentNullException.ThrowIfNull(admission);
        ArgumentNullException.ThrowIfNull(request);
        if (!ReferenceEquals(admission.Request, request) || !ReferenceEquals(execution.CurrentInvocation, admission.Invocation)
            || !HasInvocation(admission.Invocation, request) || !HasScope(admission.Act)
            || Describe(request) != admission.Act || !MatchesRequest(request, admission.Declaration)
            || declarations.GetDeclaration(admission.Act) is not { } current || !SameDeclaration(current, admission.Declaration))
            return Refused<None>();
        try
        {
            locks.RequireCurrent(admission.Plan);
            return Result.Ok(None.Value);
        }
        catch (IdentityMutationLockException)
        {
            unit.MarkRollbackOnly();
            return TenantWriteFailures.Code<None>("lockey_dependency_unavailable");
        }
    }

    public async Task<bool> AllowsIdentityContinuationAsync(TenantId tenantId, CancellationToken cancellationToken = default)
    {
        var invocation = execution.CurrentInvocation;
        var act = new SeedTenantContinuationAct(SeedTenantContinuationOperation.IdentityFoundation, tenantId);
        if (!context.IsResolved || context.TenantId != tenantId || !HasInvocation(invocation, invocation?.Operation)
            || invocation!.Policy.Id is not ("identity.role.initialize" or "identity.membership.create_first_seed_admin"
                or "identity.membership.create_seed" or "identity.membership_profile.set_seed_profile")
            || declarations.GetDeclaration(act) is not { } declaration || !HasOrdinaryOwner(tenantId)) return false;
        return await HasPristinePrerequisitesAsync(invocation!, act, declaration, requiresTheme: true, content: false, cancellationToken);
    }

    public async Task<bool> AllowsContentContinuationAsync(TenantId tenantId, bool requiresTheme,
        CancellationToken cancellationToken = default)
    {
        var invocation = execution.CurrentInvocation;
        var act = new SeedTenantContinuationAct(SeedTenantContinuationOperation.IdentityFoundation, tenantId);
        if (!context.IsResolved || context.TenantId != tenantId || !HasInvocation(invocation, invocation?.Operation, seedOnly: false)
            || !(requiresTheme ? invocation!.Policy.Id is "education.seed.create_course" or "education.seed.add_course_translation"
                    or "education.seed.publish_course" or "education.seed.create_lesson" or "education.seed.add_lesson_translation"
                    or "education.seed.publish_lesson"
                : invocation!.Policy.Id is "customization.default.initialize_content_type" or "customization.default.initialize_taxonomy"
                    or "customization.seed.register_content_type" or "customization.seed.publish_content_type"
                    or "customization.seed.register_taxonomy" or "customization.seed.publish_taxonomy")
            || declarations.GetDeclaration(act) is not { } declaration || !HasOrdinaryOwner(tenantId)) return false;
        return await HasPristinePrerequisitesAsync(invocation!, act, declaration, requiresTheme, content: true, cancellationToken);
    }

    private async Task<bool> HasPristinePrerequisitesAsync(OperationInvocation invocation, SeedTenantContinuationAct act,
        SeedTenantDeclaration declaration, bool requiresTheme, bool content, CancellationToken cancellationToken)
    {
        var transaction = unit.Transaction;
        var announcement = unit.GetTenantContextAnnouncement(transaction);
        bool Current() => ReferenceEquals(execution.CurrentInvocation, invocation)
            && HasInvocation(invocation, invocation.Operation, seedOnly: !content) && HasOrdinaryOwner(act.TenantId)
            && ReferenceEquals(unit.Transaction, transaction) && unit.GetTenantContextAnnouncement(transaction) == announcement
            && declarations.GetDeclaration(act) is { } current && SameDeclaration(current, declaration);
        var tenant = await state.ReadTenantAsync(cancellationToken);
        if (!Current() || tenant is null || !SeedTenantContinuationEvidence.IsPristinePrefix(tenant, declaration)
            || tenant.Locales.Length != declaration.OriginalLocales.Length) return false;
        var first = await state.ReadOrganizationAsync(declaration.DefaultOrganization.Id, cancellationToken);
        if (!Current() || !IsPristineOrganization(first, declaration.DefaultOrganization, declaration.TenantId)) return false;
        var second = await state.ReadOrganizationAsync(declaration.SecondOrganization.Id, cancellationToken);
        if (!Current() || !IsPristineOrganization(second, declaration.SecondOrganization, declaration.TenantId)) return false;
        var host = await state.ReadHostMappingAsync(declaration.PublicHost, cancellationToken);
        if (!Current() || !IsPristineHost(host, declaration)) return false;
        if (declaration.ThemeId is { } themeId)
        {
            var theme = await state.ReadSettingAsync(TenantSettingId.From(themeId), cancellationToken);
            if (!Current() || (requiresTheme ? !IsPristineTheme(theme, declaration) : theme is not null)) return false;
        }
        return Current();
    }

    private async Task<bool> IsPristineStageAsync(SeedTenantWriteAdmission admission, CancellationToken cancellationToken)
    {
        var declaration = admission.Declaration;
        var tenant = await state.ReadTenantAsync(cancellationToken);
        if (RequireCurrent(admission, admission.Request).IsFailure
            || tenant is null || !SeedTenantContinuationEvidence.IsPristinePrefix(tenant, declaration)) return false;
        var first = await state.ReadOrganizationAsync(declaration.DefaultOrganization.Id, cancellationToken);
        if (RequireCurrent(admission, admission.Request).IsFailure
            || !IsPristineOrganization(first, declaration.DefaultOrganization, declaration.TenantId)) return false;
        var second = await state.ReadOrganizationAsync(declaration.SecondOrganization.Id, cancellationToken);
        if (RequireCurrent(admission, admission.Request).IsFailure) return false;
        var host = await state.ReadHostMappingAsync(declaration.PublicHost, cancellationToken);
        if (RequireCurrent(admission, admission.Request).IsFailure) return false;
        var theme = declaration.ThemeId is { } themeId
            ? await state.ReadSettingAsync(TenantSettingId.From(themeId), cancellationToken) : null;
        if (RequireCurrent(admission, admission.Request).IsFailure) return false;
        switch (admission.Act.Operation)
        {
            case SeedTenantContinuationOperation.CreateOrganization:
                return tenant.Locales.IsEmpty && second is null && host is null && theme is null;
            case SeedTenantContinuationOperation.MapHost:
                return tenant.Locales.IsEmpty && IsPristineOrganization(second, declaration.SecondOrganization, declaration.TenantId)
                    && host is null && theme is null;
            case SeedTenantContinuationOperation.AddLocale:
                return IsPristineOrganization(second, declaration.SecondOrganization, declaration.TenantId)
                    && IsPristineHost(host, declaration) && theme is null
                    && tenant.Locales.Length < declaration.OriginalLocales.Length
                    && admission.Request is AddTenantLocaleCommand request && request.ExpectedVersion == tenant.Version
                    && declaration.OriginalLocales[tenant.Locales.Length] == new TenantLocaleSeedDto(request.Locale,
                        request.IsEnabled, request.IsDefault, request.Sort);
            case SeedTenantContinuationOperation.SetBranding:
                return tenant.Locales.Length == declaration.OriginalLocales.Length
                    && IsPristineOrganization(second, declaration.SecondOrganization, declaration.TenantId)
                    && IsPristineHost(host, declaration) && theme is null
                    && await customization.IsCompleteAsync(admission.Plan, cancellationToken)
                    && RequireCurrent(admission, admission.Request).IsSuccess;
            default:
                return false;
        }
    }

    private SeedTenantContinuationAct? Describe(object request) => request switch
    {
        ProvisionTenantCommand command => new(SeedTenantContinuationOperation.ProvisionTenant, command.TenantId),
        CreateOrganizationCommand command when context.IsResolved =>
            new(SeedTenantContinuationOperation.CreateOrganization, context.TenantId, command.OrganizationId.Value),
        MapHostToTenantCommand command when context.IsResolved =>
            new(SeedTenantContinuationOperation.MapHost, context.TenantId, Host: command.Host),
        AddTenantLocaleCommand command when context.IsResolved =>
            new(SeedTenantContinuationOperation.AddLocale, context.TenantId, Locale: command.Locale),
        SetTenantBrandingCommand command when context.IsResolved =>
            new(SeedTenantContinuationOperation.SetBranding, context.TenantId, command.SettingId),
        _ => null,
    };

    private bool HasInvocation(OperationInvocation? invocation, object? request, bool seedOnly = true) => request is not null
        && invocation is { IsActive: true, EntryKind: OperationEntryKind.Application }
        && ReferenceEquals(invocation.Operation, request) && ReferenceEquals(invocation.Context, context)
        && invocation.Policy.RequestType == request.GetType() && invocation.Policy.Kind == OperationPolicyKind.FiniteTrusted
        && (!seedOnly || invocation.Policy.AllowedSources.Length == 1)
        && invocation.Policy.AllowedSources.Contains(AuthorizationSourceKind.Seed)
        && invocation.TrustedSource is { SourceKind: AuthorizationSourceKind.Seed } source && source.Matches(request.GetType(), context);

    private bool HasScope(SeedTenantContinuationAct act) => context.HostScope is null
        && (context.UserId is null || context.UserId == UserId.SystemActor)
        && (act.Operation == SeedTenantContinuationOperation.ProvisionTenant
            ? !context.IsResolved : context.IsResolved && context.TenantId == act.TenantId && context.OrganizationId is null)
        && HasOrdinaryOwner(act.TenantId);

    private bool HasOrdinaryOwner(TenantId tenantId) => unit.HasActiveTransaction && !unit.IsRollbackOnly
        && unit.Mode == TransactionMode.ReadWrite && unit.Transaction is not null
        && unit is not IGlobalIdentityUnitOfWork { IsGlobalIdentityTransaction: true }
        && unit.GetTenantContextAnnouncement(unit.Transaction) is { Generation: > 0 } announcement
        && announcement.TenantId == tenantId;

    private static bool MatchesRequest(object request, SeedTenantDeclaration declaration) => request switch
    {
        ProvisionTenantCommand command => command.TenantId == declaration.TenantId && command.Slug == declaration.Slug
            && command.DisplayName == declaration.DisplayName && command.DefaultOrganizationId == declaration.DefaultOrganization.Id
            && command.DefaultOrganizationSlug == declaration.DefaultOrganization.Slug
            && command.DefaultOrganizationDisplayName == declaration.DefaultOrganization.DisplayName,
        CreateOrganizationCommand command => command.OrganizationId == declaration.SecondOrganization.Id
            && command.Slug == declaration.SecondOrganization.Slug && command.DisplayName == declaration.SecondOrganization.DisplayName,
        MapHostToTenantCommand command => command.Host == declaration.PublicHost
            && command.OrganizationId == declaration.HostOrganizationId && command.IsActive && command.IsPubliclyLive,
        AddTenantLocaleCommand command => declaration.OriginalLocales.Contains(new TenantLocaleSeedDto(command.Locale,
            command.IsEnabled, command.IsDefault, command.Sort)),
        SetTenantBrandingCommand command => command.SettingId == declaration.ThemeId && command.ExpectedVersion is null
            && command.Theme == declaration.ThemeValue,
        _ => false,
    };

    private static bool IsPristineOrganization(OrganizationSeedDto? row, SeedOrganizationDeclaration declaration, TenantId tenantId) =>
        row is not null && row.Id == declaration.Id && row.TenantId == tenantId && row.Slug == declaration.Slug
        && row.DisplayName == declaration.DisplayName && row.Status == "Active" && row.Version == 0
        && row.CreatedAt != default && row.CreatedBy == UserId.SystemActor && row.UpdatedAt is null && row.UpdatedBy is null
        && row.DeletedAt is null && row.DeletedBy is null;

    private static bool IsPristineHost(HostMappingSeedDto? row, SeedTenantDeclaration declaration) => row is not null
        && row.Host == declaration.PublicHost && row.TenantId == declaration.TenantId
        && row.OrganizationId == declaration.HostOrganizationId && row.IsActive && row.IsPubliclyLive;

    private static bool IsPristineTheme(SettingSeedDto? row, SeedTenantDeclaration declaration)
    {
        if (row is null || row.Id != declaration.ThemeId || row.TenantId != declaration.TenantId || row.OrganizationId is not null
            || row.Key != BrandingThemeRegistry.SettingKey || row.Version != 0 || declaration.ThemeValue is null
            || row.CreatedAt == default || row.CreatedBy != UserId.SystemActor || row.UpdatedAt is not null || row.UpdatedBy is not null
            || row.DeletedAt is not null || row.DeletedBy is not null) return false;
        var original = BrandingThemeRegistry.ValidateAndCanonicalize(declaration.ThemeValue);
        var current = BrandingThemeRegistry.ValidateAndCanonicalize(row.Value);
        return original.IsSuccess && current.IsSuccess && original.Value == current.Value;
    }

    private static bool SameDeclaration(SeedTenantDeclaration left, SeedTenantDeclaration right) =>
        left.TenantId == right.TenantId && left.Slug == right.Slug && left.DisplayName == right.DisplayName
        && left.DefaultOrganization == right.DefaultOrganization && left.SecondOrganization == right.SecondOrganization
        && left.OriginalLocales.SequenceEqual(right.OriginalLocales) && left.PublicHost == right.PublicHost
        && left.HostOrganizationId == right.HostOrganizationId && left.ThemeId == right.ThemeId && left.ThemeValue == right.ThemeValue;

    private static Result<T> Refused<T>() => TenantWriteFailures.Code<T>("lockey_business_rule_violation");
}

internal sealed class SeedTenantWriteAdmission(OperationInvocation invocation, object request, SeedTenantContinuationAct act,
    SeedTenantDeclaration declaration, IdentityMutationLockPlan plan)
{
    internal OperationInvocation Invocation { get; } = invocation;
    internal object Request { get; } = request;
    internal SeedTenantContinuationAct Act { get; } = act;
    internal SeedTenantDeclaration Declaration { get; } = declaration;
    internal IdentityMutationLockPlan Plan { get; } = plan;
    public override string ToString() => nameof(SeedTenantWriteAdmission);
}
