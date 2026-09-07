using LearnStack.Modules.Customization.Application.Abstractions;
using LearnStack.Modules.Customization.Application.Contracts.Customization;
using LearnStack.Modules.Customization.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Tenancy;
using LearnStack.SharedKernel.Time;
using LearnStack.SharedKernel.Validation;
using MediatR;

namespace LearnStack.Modules.Customization.Application.Customization;

/// <summary>
/// Writes a drafted content type, after its document passes ADR-0043's gates.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gate runs before the aggregate is built, not after.</b> A document that
/// will not be admitted must leave nothing behind, and building first would mean
/// the aggregate's own <c>JsonValue.EnsureWellFormed</c> throws on input the gate
/// would have refused with a JSON pointer — trading a 400 that names the mistake
/// for a 500 that does not.
/// </para>
/// <para>
/// <b>One aggregate, one write port</b>, which is what keeps this handler off
/// ADR-0042's allow-list. The generation counter is the second port and is
/// deliberately not an aggregate one — see
/// <see cref="ICustomizationGenerationStore"/> — and
/// <see cref="ITenantLevelTaxonomyCatalog"/> is the third, which asks a yes/no
/// question about the other aggregate without being able to write it.
/// </para>
/// </remarks>
internal sealed class RegisterTenantContentTypeCommandHandler(
    ITenantContentTypeStore contentTypes,
    ICustomizationGenerationStore generations,
    ITenantLevelTaxonomyCatalog taxonomies,
    IJsonSchemaValidator schemas,
    ITenantContext tenantContext,
    IClock clock)
    : IRequestHandler<RegisterTenantContentTypeCommand, Result<TenantContentTypeDto>>
{
    public async Task<Result<TenantContentTypeDto>> Handle(
        RegisterTenantContentTypeCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!tenantContext.IsResolved)
        {
            return CustomizationFailures.Unresolved<TenantContentTypeDto>();
        }

        var admitted = schemas.AdmitSchema(request.JsonSchema);

        if (admitted.IsFailure)
        {
            return CustomizationFailures.SchemaRefused<TenantContentTypeDto>(admitted.Error!);
        }

        // The gates admit LearnStack's own keywords without resolving them —
        // ADR-0043 § 4 — so the half that needs the registries happens here, before
        // anything is written. A schema naming a renderer or a taxonomy that does
        // not exist would otherwise be stored, published, and then trusted by a
        // read path that never validates.
        var unresolved = await SchemaExtensionResolution.UnresolvedAsync(
            admitted.Value!, taxonomies, cancellationToken);

        if (unresolved.Count > 0)
        {
            return CustomizationFailures.ExtensionsUnresolved<TenantContentTypeDto>(unresolved);
        }

        // The validator already proved this map builds, so From cannot throw here —
        // and it is called rather than a value carried through the command, because
        // the aggregate takes the value object and nothing else should construct one
        // on its behalf.
        var displayName = LocalizedText.From(request.DisplayName);

        var contentType = TenantContentType.Create(
            TenantContentTypeId.From(request.ContentTypeId),
            tenantContext.TenantId,
            request.Key,
            request.SchemaVersion,
            displayName,
            request.JsonSchema,
            request.RendererKey,
            clock,
            tenantContext.UserId ?? UserId.SystemActor);

        try
        {
            await contentTypes.AddAsync(contentType, cancellationToken);
        }
        catch (AggregateConflictException conflict)
        {
            var (field, reason) = CustomizationFailures.Conflict(conflict.ConstraintName);

            return CustomizationFailures.BusinessRule<TenantContentTypeDto>(field, reason);
        }

        // In this transaction, because a reader that has already composed a key
        // against the old generation must not see the new row under it.
        await generations.BumpAsync(tenantContext.TenantId, cancellationToken);

        return Result.Ok(new TenantContentTypeDto(
            contentType.Id.Value,
            contentType.TenantId,
            contentType.Key,
            contentType.SchemaVersion,
            contentType.Status.ToString()));
    }
}
