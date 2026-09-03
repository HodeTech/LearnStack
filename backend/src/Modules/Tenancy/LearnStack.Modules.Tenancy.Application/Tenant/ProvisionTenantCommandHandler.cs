using LearnStack.Modules.Tenancy.Application.Abstractions;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Identifiers;
using LearnStack.SharedKernel.Errors;
using LearnStack.SharedKernel.Localization;
using LearnStack.SharedKernel.Persistence;
using LearnStack.SharedKernel.Results;
using LearnStack.SharedKernel.Time;
using MediatR;

namespace LearnStack.Modules.Tenancy.Application.Tenant;

/// <summary>
/// Creates the tenant and its default organization, in that order, on one transaction.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the design, not a preference.</b> Measured against the shipped
/// policies: the tenant row must exist before the organization, because
/// <c>organizations</c> has a composite foreign key to <c>(tenant_id, id)</c>; and the
/// back-reference has to be a separate update, because <c>tenants.default_organization_id</c>
/// points at a row that does not exist when the tenant is inserted. Three statements, one
/// transaction, one commit — which is the whole of what ADR-0042 sanctions and the reason
/// a second connection was not an option.
/// </para>
/// <para>
/// <b>It never announces anything.</b> <c>TransactionBehavior</c> has already announced
/// the tenant this command names, by reading <c>IProvisionsTenant</c> off the request at
/// step 6. A handler that announced would be an eighth setter of <c>app.tenant_id</c>
/// against a set two ADRs close at seven, and would hand every handler in the solution
/// the ability to move the ambient tenant.
/// </para>
/// <para>
/// <b>A name already taken is an answer, not a crash.</b> Every uniqueness the schema
/// enforces here is reachable by an ordinary caller — a reused slug most of all — and
/// neither <c>DbUpdateException</c> nor <c>PostgresException</c> has an entry in
/// <c>HttpStatusMap</c>, so untranslated each one is a 500 raised after the transaction
/// was opened and the tenant announced. It cannot be pre-checked either: under the
/// provisioning announcement a <c>SELECT</c> over <c>tenants</c> returns zero rows by
/// policy, so the database's own answer is the only one available. The write store
/// translates the SQLSTATE — it is the adapter, and the only layer permitted to name the
/// provider's exception type — and this catches what it throws.
/// </para>
/// <para>
/// <b>Two ports, and the rule counts them.</b> This is the only handler in the solution
/// permitted to take more than one <c>IAggregateWriteStore</c>, which is what
/// <c>Cross_Aggregate_Writes_Are_Confined_To_Tenant_Provisioning</c> asserts. A combined
/// port would hide the cross-aggregate write from the rule that exists to count it.
/// </para>
/// </remarks>
internal sealed class ProvisionTenantCommandHandler(
    ITenantWriteStore tenants,
    IOrganizationWriteStore organizations,
    IClock clock)
    : IRequestHandler<ProvisionTenantCommand, Result<ProvisionedTenantDto>>
{
    public async Task<Result<ProvisionedTenantDto>> Handle(
        ProvisionTenantCommand request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The registry-assigned actor, not a resolved user: provisioning runs before any
        // membership exists, so there is nobody in the tenant to attribute it to. Phase
        // 03's permission check is what will identify the operator who asked.
        var actor = UserId.SystemActor;

        try
        {
            return await ProvisionAsync(request, actor, cancellationToken);
        }
        catch (AggregateConflictException conflict)
        {
            // The transaction is still usable, not aborted: EF wraps every SaveChanges
            // that runs inside a supplied transaction in a real SAVEPOINT, so a failed one
            // rolls back to its savepoint and leaves the ambient transaction alive —
            // [ADR-0040 § Frames, not savepoints](../../../../../../docs/decisions/0040-ambient-unit-of-work.md),
            // confirmed by issuing a statement on it after this catch. Returning a failure
            // is nonetheless the whole of what happens here: TransactionBehavior calls
            // FailAsync on a failure response, and a partially provisioned tenant must not
            // survive on the strength of the transaction still being open.
            var (field, reason) = ConflictFor(conflict.ConstraintName);

            return Result.FailFor<Result<ProvisionedTenantDto>>(
                new Error(
                    new LocalizedMessage("lockey_business_rule_violation"),
                    new Dictionary<string, IReadOnlyList<LocalizedMessage>>(StringComparer.Ordinal)
                    {
                        [field] = [new LocalizedMessage(reason)],
                    }));
        }
    }

    /// <summary>The three writes, in the one order the schema permits.</summary>
    private async Task<Result<ProvisionedTenantDto>> ProvisionAsync(
        ProvisionTenantCommand request, UserId actor, CancellationToken cancellationToken)
    {
        var tenant = Domain.Tenant.Create(
            request.TenantId, request.Slug, request.DisplayName, clock, actor);

        // The tenant first: `organizations` carries a composite foreign key to
        // (tenant_id, id), so its row has nothing to reference until this one lands.
        await tenants.AddAsync(tenant, cancellationToken);

        var organization = Organization.Create(
            request.DefaultOrganizationId,
            request.TenantId,
            request.DefaultOrganizationSlug,
            request.DefaultOrganizationDisplayName,
            clock,
            actor);

        await organizations.AddAsync(organization, cancellationToken);

        // Third, and it cannot be folded into the first: default_organization_id points
        // at a row that does not exist when the tenant is inserted, and the foreign key
        // behind it is MATCH SIMPLE — which skips the check only while the column is
        // null. Setting it on the insert would defeat that and fail.
        tenant.AssignDefaultOrganization(request.DefaultOrganizationId, clock, actor);
        await tenants.UpdateAsync(tenant, cancellationToken);

        return Result.Ok(new ProvisionedTenantDto(
            request.TenantId,
            tenant.Slug,
            request.DefaultOrganizationId,
            organization.Slug));
    }

    /// <summary>
    /// Which field collided, and why, as an RFC 7807 <c>errors</c> entry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The top-level code stays <c>business_rule_violation</c>, and the specificity
    /// lives in the details.</b> That is the shape <c>ValidationBehavior</c> already
    /// uses — one canonical code plus a per-field map — and the reason is not symmetry:
    /// <c>HttpStatusMap</c> is a closed table of cross-cutting codes, and a code missing
    /// from it falls through to <c>500</c>. Four module-specific keys at the top level
    /// were measured doing exactly that, which made a "slug taken" answer *worse* than
    /// the generic one it replaced — <c>business_rule_violation</c> maps to <c>409</c>.
    /// A module does not get to grow the global status table for its own vocabulary.
    /// </para>
    /// <para>
    /// By constraint name rather than a bare "already exists", because the two halves of
    /// this command fail for different reasons and a caller retrying blindly on the wrong
    /// one never succeeds: a taken slug needs a different slug, a duplicate id a different
    /// id. An unrecognised constraint names no field and says only that something is
    /// taken — a new unique index is not a reason to start crashing.
    /// </para>
    /// </remarks>
    private static (string Field, string Reason) ConflictFor(string? constraintName) =>
        constraintName switch
        {
            "ux_tenants_slug" =>
                (nameof(ProvisionTenantCommand.Slug), "lockey_slug_taken"),
            "pk_tenants" =>
                (nameof(ProvisionTenantCommand.TenantId), "lockey_identifier_taken"),
            "pk_organizations" or "ux_organizations_tenant_id_id" =>
                (nameof(ProvisionTenantCommand.DefaultOrganizationId), "lockey_identifier_taken"),

            // Unreachable on this command's own write order — the organization is inserted
            // under a tenant created moments earlier in the same transaction, so the
            // composite key's tenant half is always fresh. Kept because the port it goes
            // through is shared: the second caller of IOrganizationWriteStore.AddAsync
            // will not be provisioning, and this is the arm it needs.
            "ux_organizations_tenant_id_slug" =>
                (nameof(ProvisionTenantCommand.DefaultOrganizationSlug), "lockey_slug_taken"),

            _ => ("$", "lockey_business_rule_violation"),
        };
}
