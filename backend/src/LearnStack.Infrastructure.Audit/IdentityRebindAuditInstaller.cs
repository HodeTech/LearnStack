using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Infrastructure.Audit;

/// <summary>
/// The migration-only installer of the controlled issuer rebind's <c>audit_log</c> access (ADR-0104 § 2, § 8): the one
/// <c>INSERT</c> grant and the one policy arm of <c>learnstack_identity_rebind</c>.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="IdentityCreationWitnessInstaller"/>, this is the central owner of the global audit SQL, because no
/// module names <c>audit_log</c>. The role is not <c>BYPASSRLS</c>, so its row must satisfy the policy, and the
/// policy stays one: the existing tenant arm and the creation arm are restated unchanged and the rebind arm joins
/// them, because a second permissive policy would combine with <c>OR</c> and widen every row.
/// </para>
/// <para>
/// The existing tenant arm is restated with the rebind role excluded from it: that arm admits whatever tenant a
/// session announces, any session can announce one, and a role whose row the policy is meant to bound must not be able
/// to widen it. The rebind arm admits exactly the two rebind operations with their one outcome each, the platform sentinel tenant, the
/// identity module, no organization, no actor and no network or mail attribution, and an operator attribution in
/// <c>metadata</c>. The role holds no <c>SELECT</c>, <c>UPDATE</c> or <c>DELETE</c> on the table, and the append-only
/// guards are unchanged.
/// </para>
/// </remarks>
public static class IdentityRebindAuditInstaller
{
    /// <summary>
    /// The audit table's name, so the rebind's own posture check can name the one table the role may write without any
    /// module spelling it: the audit module alone names <c>audit_log</c>.
    /// </summary>
    public const string Table = "audit_log";

    /// <summary>Grants the <c>INSERT</c> and restates the policy with the rebind arm.</summary>
    public static void Install(MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql(InstallSql);
    }

    private const string InstallSql = """
        GRANT INSERT ON public.audit_log TO learnstack_identity_rebind;

        ALTER POLICY audit_log_isolation ON public.audit_log
            WITH CHECK (
                (current_user <> 'learnstack_identity_rebind'
                 AND tenant_id = NULLIF(current_setting('app.tenant_id',true),'')::uuid
                 AND ((organization_id IS NULL AND NULLIF(current_setting('app.organization_id',true),'') IS NULL)
                      OR organization_id = NULLIF(current_setting('app.organization_id',true),'')::uuid))
                OR (current_user='learnstack_app'
                    AND tenant_id='00000000-0000-7000-8000-000000000002'::uuid
                    AND organization_id IS NULL AND module='identity' AND operation='identity.user.create'
                    AND operation_class='Must' AND operation_type='SecurityEvent' AND entity_type='User'
                    AND outcome IN ('success','failed','indeterminate')
                    AND metadata->>'identity_source' IN ('identity-bootstrap','identity-seed-adoption')
                    AND metadata->>'identity_authority_id'=NULLIF(current_setting('app.identity_authority_id',true),'')
                    AND metadata->>'identity_subject_key'=NULLIF(current_setting('app.identity_subject_key',true),''))
                OR (current_user='learnstack_identity_rebind'
                    AND tenant_id='00000000-0000-7000-8000-000000000002'::uuid
                    AND organization_id IS NULL AND module='identity'
                    AND operation_class='Must' AND operation_type='SecurityEvent' AND entity_type='IdentityAuthority'
                    AND reason='issuer_rebind' AND actor_user_id IS NULL
                    AND actor_email IS NULL AND ip_address IS NULL AND user_agent IS NULL
                    AND metadata->'operator' IS NOT NULL
                    AND ((operation='identity.authority.rebind_attempt' AND outcome='indeterminate')
                         OR (operation='identity.authority.rebind' AND outcome='success'))));
        """;
}
