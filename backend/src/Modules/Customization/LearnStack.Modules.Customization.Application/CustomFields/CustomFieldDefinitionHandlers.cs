using System.Text;
using System.Text.Json;
using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.CustomFields;
using LearnStack.Modules.Customization.Domain;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.SharedKernel.Audit;
using LearnStack.SharedKernel.DataProtection;
using LearnStack.SharedKernel.Domain;
using LearnStack.SharedKernel.Identity;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using MediatR;
using static LearnStack.Modules.Customization.Application.CustomFields.CustomFieldDefinitionAdmission;

namespace LearnStack.Modules.Customization.Application.CustomFields;

public sealed class CustomFieldDefinitionCommandHandlers(ICustomFieldDefinitionStore store, CustomFieldDefinitionAdmission admission,
    CustomFieldFormProfile profile, ICustomFieldLocaleConfigurationReader locales, ITenantContext context,
    IAuditSubject subject, IUnitOfWork unit, IClock clock) :
    IRequestHandler<RegisterCustomFieldDefinitionCommand, Result<CustomFieldDefinitionDto>>,
    IRequestHandler<ReviseCustomFieldDefinitionDraftCommand, Result<CustomFieldDefinitionDto>>,
    IRequestHandler<PublishCustomFieldDefinitionCommand, Result<CustomFieldDefinitionDto>>,
    IRequestHandler<RetireCustomFieldDefinitionCommand, Result<CustomFieldDefinitionDto>>,
    IRequestHandler<DiscardCustomFieldDefinitionDraftCommand, Result<CustomFieldDefinitionDto>>
{
    public Task<Result<CustomFieldDefinitionDto>> Handle(RegisterCustomFieldDefinitionCommand request, CancellationToken cancellationToken) => ExecuteAsync(request, cancellationToken);
    public Task<Result<CustomFieldDefinitionDto>> Handle(ReviseCustomFieldDefinitionDraftCommand request, CancellationToken cancellationToken) => ExecuteAsync(request, cancellationToken);
    public Task<Result<CustomFieldDefinitionDto>> Handle(PublishCustomFieldDefinitionCommand request, CancellationToken cancellationToken) => ExecuteAsync(request, cancellationToken);
    public Task<Result<CustomFieldDefinitionDto>> Handle(RetireCustomFieldDefinitionCommand request, CancellationToken cancellationToken) => ExecuteAsync(request, cancellationToken);
    public Task<Result<CustomFieldDefinitionDto>> Handle(DiscardCustomFieldDefinitionDraftCommand request, CancellationToken cancellationToken) => ExecuteAsync(request, cancellationToken);

    private async Task<Result<CustomFieldDefinitionDto>> ExecuteAsync(object request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var (id, expected, input) = request switch
        {
            RegisterCustomFieldDefinitionCommand value => (value.DefinitionId, (long?)null, value.Input),
            ReviseCustomFieldDefinitionDraftCommand value => (value.DefinitionId, value.ExpectedVersion, value.Input),
            PublishCustomFieldDefinitionCommand value => (value.DefinitionId, value.ExpectedVersion, (CustomFieldDefinitionInput?)null),
            RetireCustomFieldDefinitionCommand value => (value.DefinitionId, value.ExpectedVersion, (CustomFieldDefinitionInput?)null),
            DiscardCustomFieldDefinitionDraftCommand value => (value.DefinitionId, value.ExpectedVersion, (CustomFieldDefinitionInput?)null),
            _ => throw new ArgumentException("Definition request is invalid.", nameof(request)),
        };
        if (id == Guid.Empty || id.Version != 7 || expected < 0
            || request is RegisterCustomFieldDefinitionCommand or ReviseCustomFieldDefinitionDraftCommand && input is null)
            return Fail<CustomFieldDefinitionDto>("lockey_validation_failed");
        if (request is PublishCustomFieldDefinitionCommand expectedPublication
            && ((expectedPublication.ExpectedActiveDefinitionId is null) != (expectedPublication.ExpectedActiveVersion is null)
                || expectedPublication.ExpectedActiveDefinitionId is { } incumbentId && (incumbentId == Guid.Empty || incumbentId.Version != 7)
                || expectedPublication.ExpectedActiveVersion < 0)) return Fail<CustomFieldDefinitionDto>("lockey_validation_failed");
        try
        {
            // Authoritative finite limits precede allocation, lock acquisition and fresh database checks.
            if (input is not null ? !CustomFieldFormProfile.FitsAuthoringInput(input)
                : Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, request.GetType())) > CustomFieldFormProfile.InputByteLimit)
                return Fail<CustomFieldDefinitionDto>("lockey_validation_failed");
            var admitted = await admission.PrepareAsync(request, cancellationToken);
            if (admitted.IsFailure) return Result<CustomFieldDefinitionDto>.Fail(admitted.Error);
            var plan = admitted.Value;
            TenantCustomFieldDef? target = null;
            if (expected is not null)
            {
                target = await store.FindAsync(TenantCustomFieldDefId.From(id), cancellationToken);
                if (target is null || target.DeletedAt is not null) return Fail<CustomFieldDefinitionDto>("lockey_not_found");
                subject.Designate(target);
                if (target.Version != expected) return Fail<CustomFieldDefinitionDto>("lockey_concurrency_conflict");
            }
            if (input is not null)
            {
                if (!ValidInput(input)) return Fail<CustomFieldDefinitionDto>("lockey_validation_failed");
                if (target is not null && (target.Status != CustomizationStatus.Draft || target.Key != input.Key || target.SchemaVersion != input.SchemaVersion))
                    return Fail<CustomFieldDefinitionDto>("lockey_business_rule_violation");
                var schema = profile.Admit(input.JsonSchema);
                if (schema.IsFailure) return Result<CustomFieldDefinitionDto>.Fail(schema.Error);
                if (!CustomFieldFormProfile.FitsSubmission([(input.Key, schema.Value.MaximumValueBytes)]))
                    return Fail<CustomFieldDefinitionDto>("lockey_limit_exceeded");
                var canonicalInput = input with { JsonSchema = schema.Value.Schema };
                if (!CustomFieldFormProfile.FitsAuthoringInput(canonicalInput))
                    return Fail<CustomFieldDefinitionDto>("lockey_limit_exceeded");
                var localization = await locales.ReadAsync(cancellationToken);
                if (localization.IsFailure) return Result<CustomFieldDefinitionDto>.Fail(localization.Error);
                if (!ValidLocales(input.Labels, localization.Value, 128)
                    || input.Notices is not null && !ValidLocales(input.Notices, localization.Value, 512)
                    || !CustomFieldFormProfile.ValidControlLabels(schema.Value.Schema, input.Labels, input.FormLabels,
                        localization.Value.DefaultLocale, localization.Value.EnabledLocales))
                    return Fail<CustomFieldDefinitionDto>("lockey_validation_failed");
                if (!admission.RequireCurrent(request, plan)) return Fail<CustomFieldDefinitionDto>("lockey_forbidden");
                if (target is null)
                {
                    var usage = await store.ReadUsageAsync(input.Key, cancellationToken);
                    if (usage.Revisions >= CustomFieldFormProfile.RevisionLimit || usage.Drafts >= CustomFieldFormProfile.DraftLimit
                        || usage.LatestSchemaVersion == 0 && usage.Concepts >= CustomFieldFormProfile.ConceptLimit)
                        return Fail<CustomFieldDefinitionDto>("lockey_limit_exceeded");
                    if (usage.LatestSchemaVersion == int.MaxValue || input.SchemaVersion != usage.LatestSchemaVersion + 1)
                        return Fail<CustomFieldDefinitionDto>("lockey_business_rule_violation");
                    target = TenantCustomFieldDef.Create(TenantCustomFieldDefId.From(id), context.TenantId, input.Key, input.SchemaVersion,
                        schema.Value.Schema, CanonicalMap(input.Labels), input.PiiCategory, input.IsRequired, input.Sort,
                        input.PurposeKey, input.Notices is null ? null : CanonicalMap(input.Notices), schema.Value.MaximumValueBytes, clock, context.UserId!.Value, CustomFieldFormProfile.SerializeControlLabels(input.FormLabels));
                    subject.Designate(target);
                    if (!admission.RequireCurrent(request, plan)) return Fail<CustomFieldDefinitionDto>("lockey_forbidden");
                    await store.AddAsync(target, cancellationToken);
                    return Result.Ok(ToDto(target));
                }
                target.ReviseDraft(schema.Value.Schema, CanonicalMap(input.Labels), input.PiiCategory, input.IsRequired, input.Sort,
                    input.PurposeKey, input.Notices is null ? null : CanonicalMap(input.Notices), schema.Value.MaximumValueBytes, clock, context.UserId!.Value, CustomFieldFormProfile.SerializeControlLabels(input.FormLabels));
            }
            else if (request is PublishCustomFieldDefinitionCommand)
            {
                if (target!.Status != CustomizationStatus.Draft) return Fail<CustomFieldDefinitionDto>("lockey_business_rule_violation");
                var schema = profile.RevalidateStored(target.JsonSchema);
                if (schema.IsFailure || schema.Value.MaximumValueBytes != target.MaximumValueBytes) return Fail<CustomFieldDefinitionDto>("lockey_validation_failed");
                var localization = await locales.ReadAsync(cancellationToken);
                if (localization.IsFailure) return Result<CustomFieldDefinitionDto>.Fail(localization.Error);
                if (!ValidLocales(JsonSerializer.Deserialize<SortedDictionary<string, string>>(target.LabelsJson)!, localization.Value, 128)
                    || target.NoticesJson is not null && !ValidLocales(JsonSerializer.Deserialize<SortedDictionary<string, string>>(target.NoticesJson)!, localization.Value, 512)
                    || !CustomFieldFormProfile.ValidControlLabels(target.JsonSchema,
                        JsonSerializer.Deserialize<SortedDictionary<string, string>>(target.LabelsJson)!,
                        target.FormLabelsJson is null ? null : JsonSerializer.Deserialize<List<CustomFieldControlLabels>>(target.FormLabelsJson),
                        localization.Value.DefaultLocale, localization.Value.EnabledLocales))
                    return Fail<CustomFieldDefinitionDto>("lockey_validation_failed");
                var incumbent = await store.FindActiveAsync(target.Key, cancellationToken);
                var publication = (PublishCustomFieldDefinitionCommand)request;
                if (incumbent?.Id.Value != publication.ExpectedActiveDefinitionId || incumbent?.Version != publication.ExpectedActiveVersion
                    || incumbent is not null && (incumbent.Id == target.Id || incumbent.SchemaVersion >= target.SchemaVersion))
                    return Fail<CustomFieldDefinitionDto>("lockey_concurrency_conflict");
                // A later published revision remains history even after retirement; publication never moves a concept backwards.
                if (await store.HasPublishedSuccessorAsync(target.Key, target.SchemaVersion, cancellationToken))
                    return Fail<CustomFieldDefinitionDto>("lockey_concurrency_conflict");
                var active = await store.ReadActiveAsync(cancellationToken);
                var resulting = active.Where(row => row.Key != target.Key).Append(target).ToArray();
                if (resulting.Length > CustomFieldFormProfile.ActiveLimit || !CustomFieldFormProfile.FitsSubmission(resulting
                    .Where(row => row.IsRequired).Select(row => (row.Key, row.MaximumValueBytes)))) return Fail<CustomFieldDefinitionDto>("lockey_limit_exceeded");
                if (!admission.RequireCurrent(request, plan)) return Fail<CustomFieldDefinitionDto>("lockey_forbidden");
                if (incumbent is not null) { incumbent.Retire(clock, context.UserId!.Value); await store.UpdateAsync(incumbent, cancellationToken); }
                target.Publish(clock, context.UserId!.Value);
            }
            else if (request is RetireCustomFieldDefinitionCommand)
            {
                if (target!.Status != CustomizationStatus.Active) return Fail<CustomFieldDefinitionDto>("lockey_business_rule_violation");
                target.Retire(clock, context.UserId!.Value);
            }
            else
            {
                if (target!.Status != CustomizationStatus.Draft) return Fail<CustomFieldDefinitionDto>("lockey_business_rule_violation");
                target.Discard(clock, context.UserId!.Value);
            }
            if (!admission.RequireCurrent(request, plan)) return Rollback("lockey_forbidden");
            await store.UpdateAsync(target!, cancellationToken);
            return Result.Ok(ToDto(target!));
        }
        catch (Exception failure) when (failure is AggregateConcurrencyException or AggregateConflictException or IdentityMutationLockException)
        { return Rollback(failure is IdentityMutationLockException ? "lockey_dependency_unavailable" : "lockey_concurrency_conflict"); }
        catch { if (unit.HasActiveTransaction) unit.MarkRollbackOnly(); throw; }
    }

    private Result<CustomFieldDefinitionDto> Rollback(string code)
    { if (unit.HasActiveTransaction) unit.MarkRollbackOnly(); return Fail<CustomFieldDefinitionDto>(code); }

    private static bool ValidInput(CustomFieldDefinitionInput input) => input.TargetEntity == "Membership"
        && CustomFieldFormProfile.ValidKey(input.Key) && input.SchemaVersion >= 1 && input.Sort >= 0
        && TenantCustomFieldDef.IsSupportedCategory(input.PiiCategory) && input.Labels is { Count: > 0 and <= 50 }
        && (input.PiiCategory == PersonalDataCategory.Sensitive
            ? CustomFieldFormProfile.ValidKey(input.PurposeKey) && input.PurposeKey!.Length <= CustomFieldKey.PurposeMaxLength
                && input.Notices is { Count: > 0 and <= 50 }
            : input.PurposeKey is null && input.Notices is null);
    private static bool ValidLocales(IReadOnlyDictionary<string, string> labels, CustomFieldLocaleConfiguration locales, int cap) =>
        CustomFieldFormProfile.ValidLocalizedLabels(labels, locales.DefaultLocale, locales.EnabledLocales, cap);
    private static string CanonicalMap(IReadOnlyDictionary<string, string> map) => JsonSerializer.Serialize(new SortedDictionary<string, string>(map.ToDictionary(x => x.Key, x => x.Value), StringComparer.Ordinal));
    internal static CustomFieldDefinitionDto ToDto(TenantCustomFieldDef row) => new(row.Id.Value, row.TargetEntity.ToString(), row.Key,
        row.SchemaVersion, row.Status.ToString(), row.Version, row.DeletedAt is not null, row.JsonSchema,
        JsonSerializer.Deserialize<SortedDictionary<string, string>>(row.LabelsJson)!, row.PiiCategory, row.IsRequired, row.Sort,
        row.PurposeKey, row.NoticesJson is null ? null : JsonSerializer.Deserialize<SortedDictionary<string, string>>(row.NoticesJson), row.MaximumValueBytes,
        row.FormLabelsJson is null ? null : JsonSerializer.Deserialize<List<CustomFieldControlLabels>>(row.FormLabelsJson));
}

public sealed class CustomFieldDefinitionQueryHandlers(ICustomFieldDefinitionStore store, CustomFieldDefinitionAdmission admission) :
    IRequestHandler<GetCustomFieldDefinitionQuery, Result<CustomFieldDefinitionDto>>,
    IRequestHandler<ListCustomFieldDefinitionsQuery, Result<CustomFieldConceptPage>>,
    IRequestHandler<ListCustomFieldDefinitionRevisionsQuery, Result<CustomFieldDefinitionPage>>
{
    public async Task<Result<CustomFieldDefinitionDto>> Handle(GetCustomFieldDefinitionQuery request, CancellationToken cancellationToken)
    {
        if (request.DefinitionId == Guid.Empty) return Fail<CustomFieldDefinitionDto>("lockey_validation_failed");
        var admitted = await admission.ReadAsync(request, cancellationToken); if (admitted.IsFailure) return Result<CustomFieldDefinitionDto>.Fail(admitted.Error);
        var row = await store.ReadAsync(request.DefinitionId, cancellationToken);
        if (row is null) return Fail<CustomFieldDefinitionDto>("lockey_not_found");
        var incumbent = await store.FindActiveAsync(row.Key, cancellationToken);
        var check = await admission.ReadAsync(request, cancellationToken);
        return check.IsFailure ? Result<CustomFieldDefinitionDto>.Fail(check.Error) : Result.Ok(row with
        { ActiveRevision = incumbent is null ? null : new CustomFieldActiveRevision(incumbent.Id.Value, incumbent.Version) });
    }
    public async Task<Result<CustomFieldConceptPage>> Handle(ListCustomFieldDefinitionsQuery request, CancellationToken cancellationToken)
    {
        if (request.Status is not (null or "Draft" or "Active" or "Deprecated")) return Fail<CustomFieldConceptPage>("lockey_validation_failed");
        var admitted = await admission.ReadAsync(request, cancellationToken); if (admitted.IsFailure) return Result<CustomFieldConceptPage>.Fail(admitted.Error);
        try { return Result.Ok(await store.ListConceptsAsync(request.Cursor, request.Status, cancellationToken)); }
        catch (ArgumentException) { return Fail<CustomFieldConceptPage>("lockey_validation_failed"); }
    }
    public async Task<Result<CustomFieldDefinitionPage>> Handle(ListCustomFieldDefinitionRevisionsQuery request, CancellationToken cancellationToken)
    {
        if (request.DefinitionId == Guid.Empty || request.DefinitionId.Version != 7) return Fail<CustomFieldDefinitionPage>("lockey_validation_failed");
        var admitted = await admission.ReadAsync(request, cancellationToken); if (admitted.IsFailure) return Result<CustomFieldDefinitionPage>.Fail(admitted.Error);
        var exact = await store.ReadAsync(request.DefinitionId, cancellationToken);
        if (exact is null) return Fail<CustomFieldDefinitionPage>("lockey_not_found");
        try { return Result.Ok(await store.ListRevisionsAsync(exact.Key, request.Cursor, cancellationToken)); }
        catch (ArgumentException) { return Fail<CustomFieldDefinitionPage>("lockey_validation_failed"); }
    }
}
