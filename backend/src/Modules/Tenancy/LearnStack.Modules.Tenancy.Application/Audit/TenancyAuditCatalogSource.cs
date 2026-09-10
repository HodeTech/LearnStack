using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Domain;
using LearnStack.SharedKernel.Audit;

namespace LearnStack.Modules.Tenancy.Application.Audit;

/// <summary>
/// What Tenancy audits, in code, beside the matrix that states it in prose.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only the operations whose command exists.</b>
/// <see href="../../../../../docs/modules/tenancy/audit.md">The matrix</see> carries
/// fifteen more rows marked <c>(planned)</c> — classification ahead of code — and
/// registering one of those would claim a writer that does not exist, which is the half
/// of the join that has no way to notice.
/// </para>
/// <para>
/// <b>The off-path slugs are declared by slug.</b> They belong to no request type:
/// <c>platform.admin_scope.enter</c> is entered by a service method,
/// <c>tenancy.tenant_assertion.*</c> by middleware. They are registered here because this
/// is where their matrix rows live, and <c>DeclareOffPath</c> takes the module segment
/// from the slug itself, which is what makes <c>platform.…</c> registrable from a module
/// called <c>tenancy</c>
/// (<see href="../../../../../docs/decisions/0044-audit-write-path.md">ADR-0044 Amendment
/// 3 § 1</see>).
/// </para>
/// </remarks>
public sealed class TenancyAuditCatalogSource : IAuditCatalogSource
{
    /// <inheritdoc />
    public string ModuleName => "tenancy";

    /// <inheritdoc />
    public void Describe(IAuditCatalogBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            // TWO entries for one command, and the reason is the whole of ADR-0044 § 3:
            // ProvisionTenantCommand writes two aggregate roots on one transaction and the
            // matrix classifies both MUST. Under a singular reading the Organization row
            // the matrix promises would simply never be written.
            .MustAudit<ProvisionTenantCommand>(
                "tenancy.tenant.create", OperationType.Create, typeof(Domain.Tenant))
            .MustAudit<ProvisionTenantCommand>(
                "tenancy.organization.create", OperationType.Create, typeof(Organization))

            // The same slug from a second command, which is legal and shipped: an
            // organization is created alone as well as inside provisioning, and the matrix
            // carries one row for the operation rather than one per caller.
            .MustAudit<CreateOrganizationCommand>(
                "tenancy.organization.create", OperationType.Create, typeof(Organization))

            .MustAudit<MapHostToTenantCommand>(
                "tenancy.hostmapping.write", OperationType.Create, typeof(PlatformHostMapping))

            // Off-path, by slug. Each is written by something that is not a handler, so
            // there is no type to key a registration on; each sits outside the request-type
            // join in both directions.
            // SecurityEvent, not PlatformAdmin. Its matrix cell says `security-event` and
            // gives the reason — Audit Coverage puts every platform-bypass invocation on
            // that type — and ADR-0044 § 10 and the Packet 9 scope both say the same. The
            // type is what a compliance query filters on, so the row and the corpus have
            // to name the same one.
            .DeclareOffPath(
                "platform.admin_scope.enter", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "tenancy.tenant_assertion.reject", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "tenancy.tenant_assertion.anonymous_burst",
                OperationType.SecurityEvent,
                OperationClass.Must);
    }
}
