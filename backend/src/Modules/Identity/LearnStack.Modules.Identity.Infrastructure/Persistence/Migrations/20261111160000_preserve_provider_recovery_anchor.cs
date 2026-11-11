using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace LearnStack.Modules.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("20261111160000_preserve_provider_recovery_anchor")]
public sealed class preserve_provider_recovery_anchor : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE public.identity_provider_event_scan ADD COLUMN recovery_anchor_at timestamptz;
        -- Existing state has no retained completed source bound. Use its initial
        -- activation frontier conservatively rather than invent source coverage
        -- from a later processing timestamp.
        UPDATE public.identity_provider_event_scan SET recovery_anchor_at=activation_lower+make_interval(secs=>retention_seconds);
        ALTER TABLE public.identity_provider_event_scan ALTER COLUMN recovery_anchor_at SET NOT NULL,
            ALTER COLUMN recovery_anchor_at SET DEFAULT clock_timestamp(),
            ADD CONSTRAINT ck_identity_provider_scan_recovery_anchor CHECK (isfinite(recovery_anchor_at));
        GRANT UPDATE (recovery_anchor_at) ON public.identity_provider_event_scan TO learnstack_identity_ingress;
        """);
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Provider recovery continuity requires a forward repair.");
}
