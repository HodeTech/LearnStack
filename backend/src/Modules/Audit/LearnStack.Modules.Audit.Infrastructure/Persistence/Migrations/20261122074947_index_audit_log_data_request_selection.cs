using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Audit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Serves the subject data-request statements over <c>audit_log</c> (ADR-0107 § 3, § 6; ADR-0108 § 4) by index:
    /// the rows about the person's User or Membership, and a newest-first window over the rows that carry a metadata
    /// person reference.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why.</b> The export's selection, count, retained count and metadata count ran as one <c>OR</c> over the
    /// actor, the entity and four JSON paths, which no existing index serves: each was a scan of the tenant's rows
    /// under a bounded statement budget, so a tenant with millions of rows failed every export after its retries.
    /// The statements are now unions of equality branches; the actor branch already has
    /// <c>ix_audit_log_actor_user_id_timestamp</c>.
    /// </para>
    /// <para>
    /// <b>Why no expression index.</b> Under the row policy PostgreSQL evaluates a non-leakproof qual only after the
    /// policy, and every JSON operator is non-leakproof, so an index on <c>metadata -&gt;&gt; 'causal_actor_user_id'</c>
    /// can never take the subject's value as an index condition (measured: the planner used such an index only for
    /// its tenant column and filtered every keyed row). The partial index here instead orders the keyed rows newest
    /// first, so the metadata count reads a bounded window of them.
    /// </para>
    /// <para>
    /// <b>Write cost.</b> The entity index takes an entry for every row, the one cost this table pays on every
    /// insert for the data-request read. The person-reference index is partial on the keys' presence, so a row
    /// carries an entry only when it names a causal actor or an affected user (job, delivery and provider-event
    /// rows); every other insert pays only the key tests.
    /// </para>
    /// <para>
    /// <b>Built inside the migration's transaction</b>, as the earlier ledger indexes were: there is no production
    /// data yet, and Phase 11's partition conversion recreates the indexes on the parent. Append-only is untouched:
    /// an index is no row change, and the trigger guards UPDATE, DELETE and TRUNCATE only.
    /// </para>
    /// </remarks>
    public partial class index_audit_log_data_request_selection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_audit_log_tenant_id_entity_type_entity_id_timestamp",
                table: "audit_log",
                columns: new[] { "tenant_id", "entity_type", "entity_id", "timestamp" },
                descending: new[] { false, false, false, true });

            // The predicate is repeated literally by AuditUserDataParticipant's metadata statement, which is what lets
            // the planner prove the partial index applies.
            migrationBuilder.Sql("""
                CREATE INDEX ix_audit_log_tenant_id_person_reference_timestamp
                    ON public.audit_log (tenant_id, "timestamp" DESC)
                    WHERE metadata ? 'causal_actor_user_id' OR (metadata -> 'authorization') ? 'causal_actor_user_id'
                        OR (metadata -> 'provider_event') ? 'affected_user_id';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Disposable-schema reversal only; live recovery uses a forward fix.
            migrationBuilder.Sql("DROP INDEX public.ix_audit_log_tenant_id_person_reference_timestamp;");

            migrationBuilder.DropIndex(
                name: "ix_audit_log_tenant_id_entity_type_entity_id_timestamp",
                table: "audit_log");
        }
    }
}
