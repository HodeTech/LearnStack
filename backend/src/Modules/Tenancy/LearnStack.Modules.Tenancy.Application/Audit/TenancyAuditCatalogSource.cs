using LearnStack.Modules.Tenancy.Application.Contracts.Settings;
using LearnStack.Modules.Tenancy.Application.Contracts.FieldConsents;
using LearnStack.Modules.Tenancy.Application.Contracts.Seeding;
using LearnStack.Modules.Tenancy.Application.Contracts.PublicReads;
using LearnStack.Modules.Tenancy.Application.Contracts.Locales;
using LearnStack.Modules.Tenancy.Application.Contracts.Branding;
using LearnStack.Modules.Tenancy.Application.Contracts.Tenant;
using LearnStack.Modules.Tenancy.Application.Contracts.Account;
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
/// additional rows marked <c>(planned)</c> — classification ahead of code — and
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
        builder.MustAudit<ReadOwnGrantedFieldConsentReceiptQuery>("tenancy.membership_field_consent.read_grant_receipt", OperationType.ReadSensitive, typeof(MembershipFieldConsent));
        builder.MustAudit<ReadOwnWithdrawnFieldConsentReceiptQuery>("tenancy.membership_field_consent.read_withdraw_receipt", OperationType.ReadSensitive, typeof(MembershipFieldConsent));
        builder.Off<GetOwnMembershipFieldConsentQuery>();
        builder.Off<ListOwnFieldConsentScopesQuery>();
        builder.Off<ListOwnMembershipFieldConsentsQuery>();
        builder.MustAudit<GrantOwnMembershipFieldConsentCommand>("tenancy.membership_field_consent.grant", OperationType.Create, typeof(MembershipFieldConsent));
        builder.MustAudit<WithdrawOwnMembershipFieldConsentCommand>("tenancy.membership_field_consent.withdraw", OperationType.Create, typeof(MembershipFieldConsent));
        builder.Off<GetTenantInvitationPolicyQuery>();
        builder.MustAudit<SetTenantInvitationLifetimeCommand>("tenancy.setting.write", OperationType.Update, typeof(TenantSetting));
        builder.MustAudit<ReadInvitationPolicyReceiptQuery>("tenancy.setting.read_receipt", OperationType.ReadSensitive, typeof(TenantSetting));
        builder.DeclareOffPath("tenancy.assertion.reject_claim_context", OperationType.SecurityEvent, OperationClass.Must, entityType: null);

        builder.Off<GetTenantSeedStateQuery>();
        builder.Off<GetOrganizationSeedStateQuery>();
        builder.Off<GetHostMappingSeedStateQuery>();
        builder.Off<GetSettingSeedStateQuery>();
        builder.Off<GetPublicSiteQuery>();
        builder.Off<GetAccountSiteQuery>();

        builder
            .MustAudit<ListOrganizationsQuery>("tenancy.organization.list", OperationType.ReadSensitive, typeof(Organization))
            .MustAudit<GetOrganizationDetailQuery>("tenancy.organization.read", OperationType.ReadSensitive, typeof(Organization))
            .MustAudit<ReadOrganizationReceiptQuery>("tenancy.organization.read_receipt", OperationType.ReadSensitive, typeof(Organization))
            .MustAudit<ReadDefaultOrganizationReceiptQuery>("tenancy.organization.read_receipt", OperationType.ReadSensitive, typeof(Domain.Tenant))
            .MustAudit<CreateManagedOrganizationCommand>("tenancy.organization.create", OperationType.Create, typeof(Organization))
            .ShouldAudit<RenameOrganizationCommand>("tenancy.organization.rename", OperationType.Update, typeof(Organization))
            .MustAudit<ArchiveOrganizationCommand>("tenancy.organization.archive", OperationType.Update, typeof(Organization))
            .MustAudit<SelectDefaultOrganizationCommand>("tenancy.organization.select_default", OperationType.Update, typeof(Domain.Tenant))
            .ShouldAudit<AddManagedTenantLocaleCommand>("tenancy.locale.write", OperationType.Update, typeof(Domain.Tenant))
            .ShouldAudit<SetManagedDefaultTenantLocaleCommand>("tenancy.locale.write", OperationType.Update, typeof(Domain.Tenant))
            .ShouldAudit<SetTenantLocaleEnabledCommand>("tenancy.locale.write", OperationType.Update, typeof(Domain.Tenant))
            .ShouldAudit<RemoveTenantLocaleCommand>("tenancy.locale.write", OperationType.Update, typeof(Domain.Tenant))
            .MustAudit<SetManagedTenantBrandingCommand>("tenancy.setting.write", OperationType.Update, typeof(TenantSetting))
            .ShouldAudit<AddTenantLocaleCommand>("tenancy.locale.write", OperationType.Update, typeof(Domain.Tenant))
            .ShouldAudit<SetDefaultTenantLocaleCommand>("tenancy.locale.write", OperationType.Update, typeof(Domain.Tenant))
            .MustAudit<SetTenantBrandingCommand>("tenancy.setting.write", OperationType.Update, typeof(TenantSetting))
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

            // The operator's production provisioning writes the same two rows under the same two slugs
            // (ADR-0102 § 9); the act differs in its source and in the operator it records, not in what
            // happened, so the matrix keeps one row per operation.
            .MustAudit<OperatorProvisionTenantCommand>(
                "tenancy.tenant.create", OperationType.Create, typeof(Domain.Tenant))
            .MustAudit<OperatorProvisionTenantCommand>(
                "tenancy.organization.create", OperationType.Create, typeof(Organization))
            .MustAudit<OperatorMapHostCommand>(
                "tenancy.hostmapping.write", OperationType.Create, typeof(PlatformHostMapping))

            .MustAudit<MapHostToTenantCommand>(
                "tenancy.hostmapping.write", OperationType.Create, typeof(PlatformHostMapping))
            .MustAudit<AdoptSeedAdministrationOriginCommand>(
                "tenancy.tenant.adopt_seed_origin", OperationType.SecurityEvent, typeof(Domain.Tenant))
            .MustAudit<AdoptSeedAdministrationOriginCommand>(
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
            // The killswitch flip is written inside an admitted platform scope on its work
            // transaction (ADR-0101 § 13); the scope is a service, so there is no request type.
            .DeclareOffPath(
                "tenancy.killswitch.toggle", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "platform.outbox.dead_letter", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "platform.delivery.dead_letter", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "platform.job.dead_letter", OperationType.SecurityEvent, OperationClass.Must)
            // The three operator recovery operations of ADR-0103: an inspection is audited by the platform scope's
            // entry row, whose reason is this slug's, and the two transitions write their own MUST row on the
            // outbox-role transaction that makes the change.
            .DeclareOffPath(
                "platform.delivery.inspect", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "platform.delivery.replay", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "platform.delivery.reset", OperationType.SecurityEvent, OperationClass.Must)
            // The three operator federation operations of ADR-0099 Amendment 10. A report is audited by the platform
            // scope's entry row; a remove and a disable write an intent row on their attempt slug at the scope
            // handle's checkpoint and a completion row on their own slug (Amendment 11), under the real tenant.
            .DeclareOffPath(
                "platform.federation.report", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "platform.federation.remove", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "platform.federation.remove_attempt", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "platform.federation.disable", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "platform.federation.disable_attempt", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "tenancy.tenant_assertion.reject", OperationType.SecurityEvent, OperationClass.Must)
            .DeclareOffPath(
                "tenancy.tenant_assertion.anonymous_burst",
                OperationType.SecurityEvent,
                OperationClass.Must);
    }
}
