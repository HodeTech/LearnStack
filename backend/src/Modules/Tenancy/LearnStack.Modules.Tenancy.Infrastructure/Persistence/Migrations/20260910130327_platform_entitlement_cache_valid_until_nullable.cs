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
        }

        /// <inheritdoc />
        /// <remarks>
        /// <b>Reversing this cannot preserve meaning, and the value chosen fails closed.</b>
        /// A row whose expiry is null says "no scheduled expiry"; a <c>NOT NULL</c> column
        /// has no way to say that, so every such row has to become some instant. The
        /// scaffolder's own default — <c>DateTimeOffset.MinValue</c> — is kept deliberately
        /// rather than replaced with a far-future date: it reads as long expired, so a
        /// rolled-back deployment treats an unknown entitlement as lapsed rather than as
        /// perpetual. Granting an unbounded entitlement on the way DOWN a rollback is the
        /// one outcome worse than losing the row's meaning.
        /// </remarks>
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "valid_until",
                table: "platform_entitlement_cache",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(
                    new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), TimeSpan.Zero),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
