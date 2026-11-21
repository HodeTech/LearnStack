using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence;

/// <summary>
/// The one grant the operator's federation report and sweep read through (ADR-0099 Amendment 10 item 5).
/// </summary>
/// <remarks>
/// <para>
/// <c>learnstack_platform</c> receives <c>SELECT</c> of four columns of <c>federation_connections</c>: the
/// connection's id, its tenant, its lifecycle state and its tombstone instant. That is everything the question
/// "does this broker still have a live row" needs, and nothing a tenant authored: no alias, display name, issuer,
/// provider configuration or secret reference is granted, so a statement that names one is refused by PostgreSQL
/// whatever the application does. The platform role is <c>BYPASSRLS</c>, so the grant, and not a policy, is the
/// bound; it holds no write privilege, and the table's guard trigger already admits only the application role to
/// write it.
/// </para>
/// <para>
/// The grant lives in the Identity chain because the table is created there, after the Platform chain has run.
/// </para>
/// </remarks>
internal static class FederationOperatorProjectionDdl
{
    internal static void Add(MigrationBuilder migration) => migration.Sql("""
        GRANT SELECT (id, tenant_id, state, deleted_at) ON public.federation_connections TO learnstack_platform;
        """);
}
