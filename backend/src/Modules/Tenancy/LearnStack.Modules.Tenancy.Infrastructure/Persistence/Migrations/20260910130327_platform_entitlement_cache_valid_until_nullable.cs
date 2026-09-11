using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace LearnStack.Modules.Tenancy.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Makes <c>platform_entitlement_cache.valid_until</c> nullable.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Null means "no scheduled expiry", and it is never coerced to a sentinel.</b> The
    /// wire schema <see href="../../../../../../../docs/decisions/0034-hub-contract-surface-invariant.md">ADR-0034</see>
    /// pins in both repositories makes <c>expires_at</c> required <b>and</b> nullable, the
    /// Hub's DTO carries <c>DateTimeOffset?</c>, and the Hub sends <c>null</c> for every
    /// tenant with no scheduled expiry — trials and perpetual licences, which is the cohort
    /// it creates first. Packet 6 declared the column <c>NOT NULL</c> against that
    /// contract, so the only sanctioned writer would have been structurally unable to
    /// persist what its own source sends
    /// (<see href="../../../../../../../docs/decisions/0045-entitlement-and-feature-flag-socket.md">ADR-0045
    /// Amendment 1 § 2</see>).
    /// </para>
    /// <para>
    /// A far-future date in null's place was the alternative and is refused: it would
    /// silently become an expiry somebody eventually has to explain, on a row nobody
    /// re-reads until a paying tenant stops working.
    /// </para>
    /// <para>
    /// <b>A new migration rather than an edit to the applied one.</b>
    /// <c>__EFMigrationsHistory</c> holds a migration id and a product version and no
    /// checksum, so editing Packet 6's migration is a silent no-op against an applied
    /// database and a different schema against a fresh one — undetectable in both
    /// directions. It is cheap now for the reason Amendment 1 gives: no row exists yet.
    /// </para>
    /// </remarks>
    public partial class platform_entitlement_cache_valid_until_nullable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "valid_until",
                table: "platform_entitlement_cache",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            // The first Down() installed a column DEFAULT, and ALTER COLUMN ... DROP NOT NULL
            // does not remove one — so a database reversed by it and re-applied would be left
            // nullable AND carrying a default no migration and no model snapshot declares. The
            // current Down() installs none; this line stays for a database the earlier one
            // reversed, and it is a no-op everywhere else.
            migrationBuilder.Sql(
                "ALTER TABLE platform_entitlement_cache ALTER COLUMN valid_until DROP DEFAULT;");
        }

        /// <inheritdoc />
        /// <remarks>
        /// <para>
        /// <b>A table holding a projection with no scheduled expiry refuses to be reversed,
        /// and says so.</b> A null <c>valid_until</c> means "no scheduled expiry", and a
        /// <c>NOT NULL</c> column has no way to say that. The first version of this method
        /// backfilled <c>DateTimeOffset.MinValue</c> with the scaffolder's
        /// <c>UPDATE … WHERE valid_until IS NULL</c> — which matched <b>zero rows</b>, because
        /// <c>learnstack_migration</c> is <c>NOBYPASSRLS</c> and the table is
        /// <c>FORCE ROW LEVEL SECURITY</c> — and the <c>SET NOT NULL</c> after it then failed
        /// on the rows the <c>UPDATE</c> could not see. Measured on PostgreSQL 18 by the review
        /// of Packet 9: the documented backfill never ran.
        /// </para>
        /// <para>
        /// <b>Why a refusal and not a working backfill.</b> Database Standards § Data
        /// Migrations makes a backfill tenant-aware — one <c>SET LOCAL app.tenant_id</c> per
        /// tenant — and the owner cannot enumerate the tenants to announce: every table that
        /// would list them is under the same policy, and granting the migration role a bypass
        /// is what that section names as <em>not</em> the fix. Lifting <c>FORCE</c> for the
        /// statement is the same bypass spelled differently. And the backfill itself was the
        /// weaker half: it rewrote "no expiry" as "expired in year 1", a value nobody could
        /// later tell from a real one.
        /// </para>
        /// <para>
        /// So <c>SET NOT NULL</c> runs on its own — its scan reads the heap and does not
        /// consult a policy, so it sees every row — and a null it meets becomes an error that
        /// names the table, the reason and the way out. An empty table, or one whose rows all
        /// carry an expiry, reverses exactly as Packet 6 created it. The table is a cache the
        /// Hub re-sends on its next push, which is why emptying it is the documented remedy.
        /// </para>
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    ALTER TABLE platform_entitlement_cache ALTER COLUMN valid_until SET NOT NULL;
                EXCEPTION
                    WHEN not_null_violation THEN
                        RAISE EXCEPTION 'Reversing platform_entitlement_cache_valid_until_nullable is refused: the table holds entitlement projections with no scheduled expiry, and a NOT NULL valid_until cannot represent one.'
                            USING ERRCODE = 'object_not_in_prerequisite_state',
                                  HINT = 'platform_entitlement_cache is a cache of Hub projections. Delete its rows as learnstack_platform, which holds DELETE on it, then reverse; the Hub re-sends every projection on its next push.';
                END
                $$;
                """);
        }
    }
}
