using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Audit.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes <c>audit_log.entity_id</c> <c>text</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The audit row holds the subject's key whole.</b> <c>character varying(100)</c> was a
    /// second, narrower bound on keys their own tables already bound: a host mapping's key is
    /// its host, which <c>platform_host_to_tenant</c> admits to 253 characters. A valid
    /// 101-character host failed its MUST row with <c>22001</c>, rolled the mapping back, and
    /// failed the standalone record of the attempt for the same reason — measured by the
    /// fourth review of Packet 9. Truncating the key would change which subject the row is
    /// about, so the bound goes rather than the value.
    /// </para>
    /// <para>
    /// <b>A new migration rather than an edit to the one that created the table,</b> which
    /// may already be applied to a development database; an edited migration would leave
    /// that database on the old type with nothing pending. From <c>character varying</c> to
    /// <c>text</c> is binary-coercible, so PostgreSQL changes the catalogue and rewrites no
    /// rows.
    /// </para>
    /// <para>
    /// <b>The reversal is refused once a longer key exists,</b> by PostgreSQL itself
    /// (<c>22001</c>, atomically). <c>audit_log</c> is append-only, so no such row can be
    /// shortened to let it through — which is the intended answer: a reversal that dropped or
    /// altered audit rows would be worse than one that stops.
    /// </para>
    /// </remarks>
    public partial class audit_log_entity_id_text : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "entity_id",
                table: "audit_log",
                type: "text",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "entity_id",
                table: "audit_log",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "text",
                oldNullable: true);
        }
    }
}
