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
            // The transaction is aborted at this point, so nothing may be issued on it —
            // returning a failure is what makes TransactionBehavior roll it back rather
            // than try to commit.
            return Result.FailFor<Result<ProvisionedTenantDto>>(
                new Error(new LocalizedMessage(ConflictKeyFor(conflict.ConstraintName))));
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
    /// Which uniqueness the caller collided with, as a localization key.
    /// </summary>
    /// <remarks>
    /// By constraint name rather than by a generic "already exists", because the two
    /// halves of this command fail for different reasons and a caller retrying blindly on
    /// the wrong one never succeeds: a taken slug needs a different slug, a duplicate id
    /// needs a different id. An unrecognised constraint falls back to the generic key
    /// rather than to a 500 — a new unique index is not a reason to start crashing.
    /// </remarks>
    private static string ConflictKeyFor(string? constraintName) => constraintName switch
    {
        "ux_tenants_slug" => "lockey_tenant_slug_taken",
        "pk_tenants" => "lockey_tenant_already_exists",
        "ux_organizations_tenant_id_slug" => "lockey_organization_slug_taken",
        "pk_organizations" or "ux_organizations_tenant_id_id" =>
            "lockey_organization_already_exists",
        _ => "lockey_business_rule_violation",
    };
}
